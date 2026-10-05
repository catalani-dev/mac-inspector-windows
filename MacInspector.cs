// SPDX-FileCopyrightText: 2026 CatalaniDev <catalanidev@gmail.com>
// SPDX-License-Identifier: LicenseRef-MAC-Inspector-1.0
//
// MacInspector.cs — MAC Inspector for the Windows desktop.
//
// Compiled at startup by MAC-Inspector.ps1 (Windows PowerShell 5.1, C# 5).
// Shows the vendor of a MAC address:
//   - when the mouse rests on it, in applications that expose their text
//     through UI Automation (consoles, Windows Terminal, Notepad, Office, browsers);
//   - when a text containing MAC addresses is copied (any application);
//   - on Ctrl+Alt+M, for the selected text or the clipboard;
//   - in a search window for pasted addresses and log lines.
// Everything runs locally and read-only; password fields are never read.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace MacInspector
{
    // ─── MAC detection and registry lookup (port of mac-core.js) ──────────────

    public sealed class MacMatch
    {
        public string Mac;
        public int Index;
    }

    public sealed class Special
    {
        public string Id;
        public Dictionary<string, object> Params = new Dictionary<string, object>();
        public Special(string id) { Id = id; }
        public Special With(string key, object value) { Params[key] = value; return this; }
    }

    public sealed class LookupResult
    {
        public bool Valid;
        public string Hex = "";
        public bool Complete, IsMulticast, IsLocal;
        public Special Special;
        public string Vendor, Registry, Prefix;
    }

    /// <summary>Text shown for one address. Kind drives the colour: vendor, local, group, unknown, invalid.</summary>
    public sealed class View
    {
        public string Kind, Category, Title, Detail, Hex;
        public int PrefixDigits;
        public bool Valid;
    }

    public static class Core
    {
        const string H = "[0-9A-Fa-f]";

        // Same notations and boundaries as the browser extension: one kind of separator per
        // address, and no adjacent alphanumerics or hex groups (fingerprints, EUI-64, hashes).
        static readonly string[] Formats =
        {
            "(?:" + H + "{2}:){5}" + H + "{2}",
            "(?:" + H + "{2}-){5}" + H + "{2}",
            "(?:" + H + "{4}\\.){2}" + H + "{4}",
            "(?:" + H + "{4}-){2}" + H + "{4}",
            H + "{6}-" + H + "{6}",
            H + "{12}",
        };
        static readonly string Before = "(?<![0-9A-Za-z])(?<!(?:^|[^0-9A-Za-z])(?:" + H + "{2}|" + H + "{4}|" + H + "{6})[:.\\-])";
        static readonly string After = "(?![0-9A-Za-z])(?![:.\\-]" + H + ")";
        public static readonly Regex MacRe = new Regex(Before + "(?:" + string.Join("|", Formats) + ")" + After, RegexOptions.CultureInvariant);
        static readonly Regex Separators = new Regex(@"[\s:.\-]", RegexOptions.CultureInvariant);
        static readonly Regex HexOnly = new Regex("^[0-9A-F]{6,12}$", RegexOptions.CultureInvariant);

        public static List<MacMatch> FindMacs(string text)
        {
            var list = new List<MacMatch>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in MacRe.Matches(text)) list.Add(new MacMatch { Mac = m.Value, Index = m.Index });
            return list;
        }

        public static string ToHex(string input)
        {
            return Separators.Replace(input ?? "", "").ToUpperInvariant();
        }

        /// <summary>"001C0E" → "00:1C:0E"</summary>
        public static string FormatHex(string hex)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(hex.Substring(i, Math.Min(2, hex.Length - i)));
            }
            return sb.ToString();
        }

        /// <summary>Splits the formatted address into vendor prefix and device part.</summary>
        public static string[] SplitMac(string hex, int prefixDigits)
        {
            string full = FormatHex(hex);
            int n = Math.Min(Math.Max(prefixDigits > 0 ? prefixDigits : 6, 1), hex.Length);
            int cut = Math.Min(n + (n - 1) / 2, full.Length);
            return new[] { full.Substring(0, cut), full.Substring(cut) };
        }

        public static Special SpecialAddress(string hex)
        {
            Func<int, int, int> byteAt = (from, to) => Convert.ToInt32(hex.Substring(from, to - from), 16);
            if (hex == "FFFFFFFFFFFF") return new Special("broadcast");
            if (hex == "000000000000") return new Special("null");
            if (hex.StartsWith("01005E", StringComparison.Ordinal)) return new Special(byteAt(6, 8) < 0x80 ? "ipv4Multicast" : "ianaReserved");
            if (hex.StartsWith("3333", StringComparison.Ordinal)) return new Special("ipv6Multicast");
            if (hex.StartsWith("0180C200000", StringComparison.Ordinal))
            {
                switch (byteAt(10, 12))
                {
                    case 0x00: return new Special("stp");
                    case 0x01: return new Special("pause");
                    case 0x02: return new Special("slowProtocols");
                    case 0x03: return new Special("pae");
                    case 0x0E: return new Special("lldp");
                    default: return new Special("linkLocalReserved");
                }
            }
            if (hex == "01000CCCCCCC") return new Special("cdp");
            if (hex == "01000CCCCCCD") return new Special("pvst");
            if (hex.StartsWith("00005E0001", StringComparison.Ordinal)) return new Special("vrrp4").With("id", byteAt(10, 12));
            if (hex.StartsWith("00005E0002", StringComparison.Ordinal)) return new Special("vrrp6").With("id", byteAt(10, 12));
            if (hex.StartsWith("00000C07AC", StringComparison.Ordinal)) return new Special("hsrp1").With("group", byteAt(10, 12));
            if (hex.StartsWith("00000C9FF", StringComparison.Ordinal)) return new Special("hsrp2").With("group", byteAt(9, 12));
            return null;
        }

        static readonly Dictionary<string, int> BlockBits = new Dictionary<string, int> { { "MA-L", 24 }, { "MA-M", 28 }, { "MA-S", 36 }, { "CID", 24 } };

        public static View Describe(LookupResult r)
        {
            if (r == null || !r.Valid)
                return new View { Kind = "invalid", Category = Texts.T("kind.invalid.category"), Title = Texts.T("kind.invalid.title"), Detail = Texts.T("kind.invalid.detail"), Hex = "" };

            var v = new View { Valid = true, Hex = r.Hex, PrefixDigits = r.Prefix != null ? r.Prefix.Length : 6 };
            string block = r.Prefix != null
                ? Texts.T("kind.block", "registry", r.Registry, "bits", BlockBits[r.Registry], "prefix", FormatHex(r.Prefix))
                : "";
            if (r.Special != null)
            {
                var args = new List<object>();
                foreach (var kv in r.Special.Params) { args.Add(kv.Key); args.Add(kv.Value); }
                v.Kind = "group"; v.Category = Texts.T("kind.special.category");
                v.Title = Texts.T("special." + r.Special.Id, args.ToArray());
                v.Detail = r.Vendor != null ? Texts.T("kind.special.assigned", "vendor", r.Vendor) : "";
            }
            else if (r.IsLocal)
            {
                v.Kind = "local";
                if (r.Vendor != null) { v.Category = Texts.T("kind.localVendor.category"); v.Title = r.Vendor; v.Detail = block; }
                else { v.Category = Texts.T("kind.random.category"); v.Title = Texts.T("kind.random.title"); v.Detail = Texts.T("kind.random.detail"); }
            }
            else if (r.IsMulticast)
            {
                v.Kind = "group"; v.Category = Texts.T("kind.group.category");
                v.Title = r.Vendor ?? Texts.T("kind.group.unregistered"); v.Detail = r.Vendor != null ? block : "";
            }
            else if (r.Vendor != null)
            {
                v.Kind = "vendor"; v.Category = Texts.T("kind.vendor.category"); v.Title = r.Vendor; v.Detail = block;
            }
            else
            {
                v.Kind = "unknown"; v.Category = Texts.T("kind.unknown.category"); v.Title = Texts.T("kind.unknown.title");
                v.Detail = Texts.T("kind.unknown.detail", "prefix", FormatHex(r.Hex.Substring(0, 6)));
            }
            return v;
        }

        internal static bool IsHexPrefix(string hex) { return HexOnly.IsMatch(hex); }
    }

    /// <summary>The bundled IEEE registry (oui-data.json, built by the extension's build script).</summary>
    public sealed class OuiRegistry
    {
        readonly Dictionary<string, string> L, M, S, CID;
        public readonly string Generated;
        public readonly int Total;

        OuiRegistry(Dictionary<string, object> root)
        {
            L = Table(root, "L"); M = Table(root, "M"); S = Table(root, "S"); CID = Table(root, "CID");
            object meta;
            if (root.TryGetValue("meta", out meta) && meta is Dictionary<string, object>)
            {
                object g; if (((Dictionary<string, object>)meta).TryGetValue("generated", out g)) Generated = g as string;
            }
            Total = L.Count + M.Count + S.Count;
        }

        static Dictionary<string, string> Table(Dictionary<string, object> root, string key)
        {
            var t = new Dictionary<string, string>(StringComparer.Ordinal);
            object o;
            if (root.TryGetValue(key, out o) && o is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)o) { var s = kv.Value as string; if (s != null) t[kv.Key] = s; }
            return t;
        }

        public static OuiRegistry Load(string path)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = js.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("oui-data.json is not a JSON object");
            return new OuiRegistry(root);
        }

        /// <summary>Longest-prefix match (MA-S → MA-M → MA-L); locally administered addresses use CID.</summary>
        public LookupResult Lookup(string input)
        {
            string hex = Core.ToHex(input);
            if (!Core.IsHexPrefix(hex)) return new LookupResult { Valid = false };
            int first = Convert.ToInt32(hex.Substring(0, 2), 16);
            var r = new LookupResult
            {
                Valid = true, Hex = hex, Complete = hex.Length == 12,
                IsMulticast = (first & 0x01) != 0, IsLocal = (first & 0x02) != 0,
                Special = hex.Length == 12 ? Core.SpecialAddress(hex) : null,
            };
            // For group addresses the owner is found by clearing the I/G bit
            string b = (first & 0xFE).ToString("X2") + hex.Substring(2);
            Func<string, Dictionary<string, string>, int, bool> tryTable = (name, table, len) =>
            {
                string vendor;
                if (b.Length < len || !table.TryGetValue(b.Substring(0, len), out vendor)) return false;
                r.Vendor = vendor; r.Registry = name; r.Prefix = b.Substring(0, len);
                return true;
            };
            if (r.IsLocal) tryTable("CID", CID, 6);
            else if (!tryTable("MA-S", S, 9) && !tryTable("MA-M", M, 7)) tryTable("MA-L", L, 6);
            return r;
        }
    }

    // ─── Texts ─────────────────────────────────────────────────────────────────

    public static class Texts
    {
        public static string Lang = "en";
        static readonly Dictionary<string, Dictionary<string, string>> Dict = new Dictionary<string, Dictionary<string, string>>
        {
            { "en", new Dictionary<string, string>() }, { "it", new Dictionary<string, string>() },
        };
        static readonly Regex Placeholder = new Regex(@"\{(\w+)\}");

        static void Add(string key, string en, string it) { Dict["en"][key] = en; Dict["it"][key] = it; }

        static Texts()
        {
            Add("kind.invalid.category", "Invalid format", "Formato non valido");
            Add("kind.invalid.title", "Not a MAC address", "Non è un MAC");
            Add("kind.invalid.detail", "Enter 6 to 12 hexadecimal digits, with or without separators", "Scrivi da 6 a 12 cifre esadecimali, con o senza separatori");
            Add("kind.block", "{registry} block, {bits} bits: {prefix}", "Blocco {registry} da {bits} bit: {prefix}");
            Add("kind.special.category", "Special address", "Indirizzo speciale");
            Add("kind.special.assigned", "Assigned to {vendor}", "Assegnato a {vendor}");
            Add("kind.localVendor.category", "Locally administered", "Amministrato localmente");
            Add("kind.random.category", "Random or virtual MAC", "MAC casuale o virtuale");
            Add("kind.random.title", "Vendor cannot be determined", "Vendor non ricavabile");
            Add("kind.random.detail", "Local bit set: typical of private addresses on phones, VMs and containers", "Bit locale attivo: tipico di MAC privati di smartphone, VM e container");
            Add("kind.group.category", "Group address", "Indirizzo di gruppo");
            Add("kind.group.unregistered", "Unregistered group", "Gruppo non registrato");
            Add("kind.vendor.category", "Registered vendor", "Vendor registrato");
            Add("kind.unknown.category", "Not registered", "Non registrato");
            Add("kind.unknown.title", "Unknown vendor", "Vendor sconosciuto");
            Add("kind.unknown.detail", "Prefix {prefix} is not in the IEEE registry", "Il prefisso {prefix} non è nel registro IEEE");

            Add("special.broadcast", "Broadcast", "Broadcast");
            Add("special.null", "Null address", "Indirizzo nullo");
            Add("special.ipv4Multicast", "IPv4 multicast (IANA)", "Multicast IPv4 (IANA)");
            Add("special.ianaReserved", "Reserved IANA multicast", "Multicast IANA riservato");
            Add("special.ipv6Multicast", "IPv6 multicast", "Multicast IPv6");
            Add("special.stp", "STP / Bridge Group (IEEE 802.1D)", "STP / Bridge Group (IEEE 802.1D)");
            Add("special.pause", "Pause frame (IEEE 802.3x)", "Pause frame (IEEE 802.3x)");
            Add("special.slowProtocols", "Slow Protocols: LACP / OAM (IEEE 802.3)", "Slow Protocols: LACP / OAM (IEEE 802.3)");
            Add("special.pae", "802.1X PAE / LLDP (Nearest non-TPMR Bridge)", "802.1X PAE / LLDP (Nearest non-TPMR Bridge)");
            Add("special.lldp", "LLDP / PTP (Nearest Bridge)", "LLDP / PTP (Nearest Bridge)");
            Add("special.linkLocalReserved", "Reserved IEEE 802.1 link-local", "IEEE 802.1 link-local riservato");
            Add("special.cdp", "Cisco CDP / VTP / DTP / UDLD", "Cisco CDP / VTP / DTP / UDLD");
            Add("special.pvst", "Cisco PVST+", "Cisco PVST+");
            Add("special.vrrp4", "VRRP IPv4 (VRID {id})", "VRRP IPv4 (VRID {id})");
            Add("special.vrrp6", "VRRP IPv6 (VRID {id})", "VRRP IPv6 (VRID {id})");
            Add("special.hsrp1", "HSRP v1 (group {group})", "HSRP v1 (gruppo {group})");
            Add("special.hsrp2", "HSRP v2 (group {group})", "HSRP v2 (gruppo {group})");

            Add("tray.tooltip", "MAC Inspector\nCtrl+Alt+M: identify selection or clipboard", "MAC Inspector\nCtrl+Alt+M: identifica selezione o appunti");
            Add("tray.search", "Search…", "Cerca…");
            Add("tray.hover", "Show vendor on hover", "Mostra il vendor al passaggio del cursore");
            Add("tray.clipboard", "Identify copied MAC addresses", "Riconosci i MAC copiati");
            Add("tray.language", "Language", "Lingua");
            Add("tray.langAuto", "Automatic", "Automatica");
            Add("tray.about", "License and credits", "Licenza e crediti");
            Add("tray.exit", "Exit", "Esci");
            Add("tray.started", "Running in the notification area. Hover over a MAC address, or press Ctrl+Alt+M on a selection.", "In esecuzione nell'area di notifica. Passa il cursore su un MAC oppure premi Ctrl+Alt+M su una selezione.");
            Add("tray.hotkeyBusy", "Ctrl+Alt+M is already used by another program: use the Search window instead.", "Ctrl+Alt+M è già usato da un altro programma: usa la finestra Cerca.");

            Add("search.title", "MAC Inspector", "MAC Inspector");
            Add("search.label", "Paste MAC addresses or lines of text", "Incolla MAC o righe di testo");
            Add("search.count", "{count} addresses", "{count} indirizzi");
            Add("search.one", "1 address", "1 indirizzo");
            Add("search.none", "No MAC address in this text", "Nessun MAC in questo testo");
            Add("search.recent", "Recently identified", "Identificati di recente");
            Add("search.empty", "Addresses you identify will be listed here.", "Qui compariranno gli indirizzi identificati.");
            Add("search.nothingFound", "No MAC address in the selection or clipboard.", "Nessun MAC nella selezione o negli appunti.");
            Add("search.footer", "Ctrl+Alt+M: selection or clipboard · Data: IEEE, {date}", "Ctrl+Alt+M: selezione o appunti · Dati: IEEE, {date}");

            Add("card.copy", "Click to copy", "Clic per copiare");
            Add("card.copied", "Copied", "Copiato");
            Add("card.more", "+{count} more: Ctrl+Alt+M opens the list", "+ altri {count}: Ctrl+Alt+M apre l'elenco");
        }

        /// <summary>Translates a key; args are name/value pairs. Values are inserted literally.</summary>
        public static string T(string key, params object[] args)
        {
            string text;
            if (!Dict[Lang].TryGetValue(key, out text) && !Dict["en"].TryGetValue(key, out text)) return key;
            if (args == null || args.Length == 0) return text;
            var map = new Dictionary<string, string>();
            for (int i = 0; i + 1 < args.Length; i += 2) map[Convert.ToString(args[i], CultureInfo.InvariantCulture)] = Convert.ToString(args[i + 1], CultureInfo.InvariantCulture);
            return Placeholder.Replace(text, m => map.ContainsKey(m.Groups[1].Value) ? map[m.Groups[1].Value] : m.Value);
        }

        public static string Resolve(string setting)
        {
            if (setting == "en" || setting == "it") return setting;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
        }
    }

    // ─── Settings ──────────────────────────────────────────────────────────────

    public sealed class Settings
    {
        public bool Hover = true, Clipboard = true, Welcomed;
        public string Lang = "auto";
        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MAC Inspector Desktop"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (d == null) return s;
                object o;
                if (d.TryGetValue("hover", out o) && o is bool) s.Hover = (bool)o;
                if (d.TryGetValue("clipboard", out o) && o is bool) s.Clipboard = (bool)o;
                if (d.TryGetValue("welcomed", out o) && o is bool) s.Welcomed = (bool)o;
                if (d.TryGetValue("lang", out o) && (o as string == "en" || o as string == "it" || o as string == "auto")) s.Lang = (string)o;
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var d = new Dictionary<string, object> { { "hover", Hover }, { "clipboard", Clipboard }, { "welcomed", Welcomed }, { "lang", Lang } };
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(d));
            }
            catch { }
        }
    }

    // ─── Native ────────────────────────────────────────────────────────────────

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string idList);

        public static readonly IntPtr DpiPerMonitorV2 = new IntPtr(-4);
        public static readonly IntPtr DpiSystemAware = new IntPtr(-2);

        public static Point Cursor()
        {
            POINT p; GetCursorPos(out p); return new Point(p.X, p.Y);
        }

        public static float ScaleAt(Point pt)
        {
            uint dx, dy;
            try
            {
                IntPtr mon = MonitorFromPoint(new POINT { X = pt.X, Y = pt.Y }, 2);
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0) return dx / 96f;
            }
            catch { }
            return 1f;
        }

        public static Rectangle WorkAreaAt(Point pt)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            IntPtr mon = MonitorFromPoint(new POINT { X = pt.X, Y = pt.Y }, 2);
            if (!GetMonitorInfo(mon, ref mi)) return new Rectangle(0, 0, 1920, 1080);
            return Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
        }

        public static int ProcessAt(Point pt)
        {
            uint pid; GetWindowThreadProcessId(WindowFromPoint(new POINT { X = pt.X, Y = pt.Y }), out pid); return (int)pid;
        }

        /// <summary>Runs code with a different DPI awareness on the current thread (windows keep the one they were created with).</summary>
        public static T WithDpi<T>(IntPtr context, Func<T> create)
        {
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(context); } catch { }
            try { return create(); }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }
    }

    // ─── Reading text through UI Automation ───────────────────────────────────

    public sealed class HoverHit
    {
        public string Mac;
        public Rectangle Rect;
        public long Ms; // time taken by the UI Automation query
    }

    static class UiaText
    {
        static readonly Condition HasText = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
        static readonly Dictionary<string, KeyValuePair<DateTime, AutomationElement>> HostCache = new Dictionary<string, KeyValuePair<DateTime, AutomationElement>>();

        /// <summary>MAC address under the point, or null. Uses the text line under the point and the
        /// on-screen rectangle of each MAC in it: some providers return the row but not the column.</summary>
        public static string LastProbe = "";

        public static HoverHit Probe(Point p, int ownPid)
        {
            var pt = new System.Windows.Point(p.X, p.Y);
            AutomationElement el;
            LastProbe = "";
            try { el = AutomationElement.FromPoint(pt); } catch (Exception e) { LastProbe = "FromPoint " + e.GetType().Name; return null; }
            if (el == null) { LastProbe = "no element"; return null; }
            try { LastProbe = el.Current.ClassName + "/" + el.Current.ControlType.ProgrammaticName + " pid " + el.Current.ProcessId; } catch { }
            try
            {
                AutomationElement.AutomationElementInformation cur = el.Current;
                if (cur.ProcessId == ownPid || cur.IsPassword) return null;

                AutomationElement host;
                TextPattern tp = FindTextPattern(el, pt, out host);
                if (tp != null)
                {
                    if (host != null && host.Current.IsPassword) return null;
                    TextPatternRange line = tp.RangeFromPoint(pt).Clone();
                    line.ExpandToEnclosingUnit(TextUnit.Line);
                    string text = line.GetText(2000);
                    LastProbe += " text via " + (host != null ? host.Current.ClassName : "?") + " line=" + text.Length + " macs=" + Core.FindMacs(text).Count;
                    var done = new HashSet<string>();
                    foreach (MacMatch m in Core.FindMacs(text))
                    {
                        if (!done.Add(m.Mac)) continue;
                        // Search again after the first match only when the address really repeats in the
                        // line: some providers (Windows Terminal) answer a search that finds nothing slowly.
                        int occurrences = 0;
                        foreach (MacMatch o in Core.FindMacs(text)) if (o.Mac == m.Mac) occurrences++;
                        TextPatternRange search = line.Clone();
                        for (int i = 0; i < Math.Min(occurrences, 4); i++)
                        {
                            try
                            {
                                TextPatternRange r = search.FindText(m.Mac, false, false);
                                if (r == null) break;
                                foreach (System.Windows.Rect rc in r.GetBoundingRectangles())
                                {
                                    if (p.X >= rc.Left - 3 && p.X <= rc.Right + 3 && p.Y >= rc.Top - 2 && p.Y <= rc.Bottom + 2)
                                        return new HoverHit { Mac = m.Mac, Rect = new Rectangle((int)rc.Left, (int)rc.Top, (int)Math.Ceiling(rc.Width), (int)Math.Ceiling(rc.Height)) };
                                }
                                if (i + 1 < occurrences) search.MoveEndpointByRange(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End);
                            }
                            catch (Exception e) { LastProbe += " find " + e.GetType().Name; break; }
                        }
                    }
                    return null;
                }

                // Elements without text support (labels, cells): only small ones holding exactly one MAC
                string s = null;
                object vp;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) s = ((ValuePattern)vp).Current.Value;
                if (string.IsNullOrEmpty(s)) s = cur.Name;
                if (string.IsNullOrEmpty(s) || s.Length > 300) return null;
                var found = Core.FindMacs(s);
                System.Windows.Rect b = cur.BoundingRectangle;
                if (found.Count == 1 && !b.IsEmpty && b.Width < 900 && b.Height < 80)
                    return new HoverHit { Mac = found[0].Mac, Rect = new Rectangle((int)b.Left, (int)b.Top, (int)b.Width, (int)b.Height) };
            }
            catch (Exception e) { LastProbe += " error " + e.GetType().Name; }
            return null;
        }

        static TextPattern FindTextPattern(AutomationElement el, System.Windows.Point pt, out AutomationElement host)
        {
            host = null;
            object p;
            AutomationElement node = el;
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int i = 0; i < 8 && node != null && node != AutomationElement.RootElement; i++)
            {
                if (node.TryGetCurrentPattern(TextPattern.Pattern, out p)) { host = node; return (TextPattern)p; }
                node = walker.GetParent(node);
            }

            // Containers such as XAML islands (Windows Terminal) are returned instead of the text
            // element: look below them, only into children under the point and only a few levels
            // deep, so large trees (Electron and browser windows) stay cheap. Cached per container.
            string key = string.Join(".", el.GetRuntimeId());
            KeyValuePair<DateTime, AutomationElement> cached;
            AutomationElement candidate = null;
            if (HostCache.TryGetValue(key, out cached) && (DateTime.UtcNow - cached.Key).TotalSeconds < 5) candidate = cached.Value;
            else
            {
                candidate = DescendAt(el, pt, walker);
                if (HostCache.Count > 64) HostCache.Clear();
                HostCache[key] = new KeyValuePair<DateTime, AutomationElement>(DateTime.UtcNow, candidate);
            }
            if (candidate != null && candidate.Current.BoundingRectangle.Contains(pt) && candidate.TryGetCurrentPattern(TextPattern.Pattern, out p))
            {
                host = candidate; return (TextPattern)p;
            }
            return null;
        }

        /// <summary>Breadth-first search for a text element under the point: at most 4 levels and 80 elements.</summary>
        static AutomationElement DescendAt(AutomationElement root, System.Windows.Point pt, TreeWalker walker)
        {
            var level = new List<AutomationElement> { root };
            int visited = 0;
            for (int depth = 0; depth < 4 && level.Count > 0; depth++)
            {
                var next = new List<AutomationElement>();
                foreach (AutomationElement parent in level)
                {
                    for (AutomationElement c = walker.GetFirstChild(parent); c != null && visited < 80; c = walker.GetNextSibling(c))
                    {
                        visited++;
                        if (!c.Current.BoundingRectangle.Contains(pt)) continue;
                        if ((bool)c.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty)) return c;
                        next.Add(c);
                    }
                }
                level = next;
            }
            return null;
        }

        public static string LastSelectionInfo = "";

        static string SelectedText(TextPattern tp)
        {
            var sb = new StringBuilder();
            foreach (TextPatternRange r in tp.GetSelection()) sb.AppendLine(r.GetText(20000));
            string s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        /// <summary>Text selected in another application, or null. Looks at the focused element first,
        /// then at the text elements of the foreground window: releasing Alt can move the focus to a
        /// menu (Notepad on Windows 11 does) while the selection stays in the document.</summary>
        public static string Selection(int ownPid)
        {
            try
            {
                AutomationElement node = AutomationElement.FocusedElement;
                LastSelectionInfo = node == null ? "no focus" : node.Current.ControlType.ProgrammaticName;
                if (node != null && node.Current.ProcessId != ownPid && !node.Current.IsPassword)
                {
                    TreeWalker walker = TreeWalker.ControlViewWalker;
                    for (int i = 0; i < 4 && node != null && node != AutomationElement.RootElement; i++)
                    {
                        object p;
                        if (node.TryGetCurrentPattern(TextPattern.Pattern, out p))
                        {
                            string s = SelectedText((TextPattern)p);
                            if (s != null) { LastSelectionInfo += " focus"; return s; }
                            break;
                        }
                        node = walker.GetParent(node);
                    }
                }

                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return null;
                AutomationElement win = AutomationElement.FromHandle(fg);
                if (win == null || win.Current.ProcessId == ownPid) return null;
                var docs = new List<AutomationElement>();
                var others = new List<AutomationElement>();
                foreach (AutomationElement d in win.FindAll(TreeScope.Descendants, HasText))
                {
                    ControlType ct = d.Current.ControlType;
                    if (ct == ControlType.Document || ct == ControlType.Edit) docs.Add(d); else others.Add(d);
                }
                docs.AddRange(others);
                for (int i = 0; i < docs.Count && i < 40; i++)
                {
                    if (docs[i].Current.IsPassword) continue;
                    object p;
                    if (!docs[i].TryGetCurrentPattern(TextPattern.Pattern, out p)) continue;
                    string s = SelectedText((TextPattern)p);
                    if (s != null) { LastSelectionInfo += " window"; return s; }
                }
            }
            catch (Exception e) { LastSelectionInfo += " error " + e.GetType().Name; }
            return null;
        }
    }

    // ─── Hover engine ─────────────────────────────────────────────────────────

    /// <summary>Background thread that watches the cursor and asks UI Automation for the text under it
    /// once the cursor has rested for a short time.</summary>
    sealed class HoverEngine
    {
        const int TickMs = 50, DwellMs = 180;
        readonly int ownPid = Process.GetCurrentProcess().Id;
        readonly Action<HoverHit> onHit;
        readonly Action onClear;
        public Action<string> OnProbe; // test diagnostics only
        volatile bool running = true;
        public volatile bool Enabled = true;
        Rectangle shown = Rectangle.Empty;
        public Rectangle CardBounds = Rectangle.Empty; // written by the UI thread

        public HoverEngine(Action<HoverHit> onHit, Action onClear)
        {
            this.onHit = onHit; this.onClear = onClear;
            var t = new Thread(Loop) { IsBackground = true, Name = "MacInspector.Hover" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        public void Stop() { running = false; }

        void Clear()
        {
            if (shown.IsEmpty) return;
            shown = Rectangle.Empty;
            onClear();
        }

        void Loop()
        {
            try { Native.SetThreadDpiAwarenessContext(Native.DpiPerMonitorV2); } catch { }
            Point last = Point.Empty;
            var still = Stopwatch.StartNew();
            bool probed = false;
            while (running)
            {
                Thread.Sleep(TickMs);
                if (!Enabled) { Clear(); continue; }
                Point p = Native.Cursor();
                if (p != last)
                {
                    last = p; still.Restart(); probed = false;
                    Rectangle keep = shown; keep.Inflate(4, 4);
                    if (!shown.IsEmpty && !keep.Contains(p) && !CardBounds.Contains(p)) Clear();
                    continue;
                }
                bool buttons = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (probed || buttons || still.ElapsedMilliseconds < DwellMs) continue;
                probed = true;
                if (!shown.IsEmpty) continue; // still over the address already shown
                if (Native.ProcessAt(p) == ownPid) continue;
                var timer = Stopwatch.StartNew();
                HoverHit hit = UiaText.Probe(p, ownPid);
                if (OnProbe != null) OnProbe(p.X + "," + p.Y + " " + timer.ElapsedMilliseconds + "ms " + UiaText.LastProbe + (hit != null ? " HIT" : ""));
                if (hit != null) { hit.Ms = timer.ElapsedMilliseconds; shown = hit.Rect; onHit(hit); }
            }
        }
    }

    // ─── Card drawing ─────────────────────────────────────────────────────────

    sealed class Palette
    {
        public Color Back, Surface, Ink, Muted, Line;
        readonly Dictionary<string, Color> kinds = new Dictionary<string, Color>();
        public bool Dark;

        public Color Kind(string kind) { Color c; return kinds.TryGetValue(kind ?? "", out c) ? c : Muted; }

        /// <summary>Colours of the extension (ui.css), following the Windows app theme.</summary>
        public static Palette Current()
        {
            bool dark = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch { }
            var p = new Palette { Dark = dark };
            Func<string, Color> c = ColorTranslator.FromHtml;
            if (dark)
            {
                p.Back = c("#1B1E23"); p.Surface = c("#262A31"); p.Ink = c("#E7E9EC"); p.Muted = c("#A1A9B3"); p.Line = c("#373C45");
                p.kinds["vendor"] = c("#7AA7FF"); p.kinds["local"] = c("#F0A45A"); p.kinds["group"] = c("#AE95FF"); p.kinds["unknown"] = c("#9AA3AE"); p.kinds["invalid"] = c("#FF8080");
            }
            else
            {
                p.Back = c("#ECEEF1"); p.Surface = c("#FFFFFF"); p.Ink = c("#1D2127"); p.Muted = c("#5B6470"); p.Line = c("#D3D8DE");
                p.kinds["vendor"] = c("#2F6FDF"); p.kinds["local"] = c("#AD5710"); p.kinds["group"] = c("#7353CC"); p.kinds["unknown"] = c("#6B737D"); p.kinds["invalid"] = c("#C23B3B");
            }
            return p;
        }
    }

    /// <summary>Lays out and paints the "tape" cards of the extension: colour stripe, category,
    /// vendor, address with the vendor prefix in bold, registry block.</summary>
    sealed class CardLayout : IDisposable
    {
        public readonly List<View> Views;
        public readonly List<Rectangle> EntryRects = new List<Rectangle>();
        public string Footer;
        public Size Size;
        readonly float s;
        readonly Font fCat, fTitle, fMono, fMonoBold, fSmall;
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        static string monoFamily;

        static string MonoFamily()
        {
            if (monoFamily != null) return monoFamily;
            monoFamily = "Consolas";
            using (var fonts = new InstalledFontCollection())
                foreach (FontFamily f in fonts.Families) if (f.Name == "Cascadia Mono") { monoFamily = f.Name; break; }
            return monoFamily;
        }

        static Font F(string family, float pt, FontStyle style, float scale)
        {
            return new Font(family, pt * 96f / 72f * scale, style, GraphicsUnit.Pixel);
        }

        int Pad { get { return (int)(12 * s); } }
        int PadV { get { return (int)(8 * s); } }
        int Stripe { get { return Math.Max(3, (int)(4 * s)); } }
        int Gap { get { return (int)(2 * s); } }

        public CardLayout(List<View> views, string footer, float scale, int maxWidth, bool fill = false)
        {
            Views = views; Footer = footer; s = scale;
            fCat = F("Segoe UI", 8f, FontStyle.Bold, s);
            fTitle = F("Segoe UI", 11f, FontStyle.Bold, s);
            fMono = F(MonoFamily(), 10f, FontStyle.Regular, s);
            fMonoBold = F(MonoFamily(), 10f, FontStyle.Bold, s);
            fSmall = F("Segoe UI", 8.5f, FontStyle.Regular, s);

            int contentW = (int)(200 * s);
            int y = 0;
            var big = new Size(int.MaxValue, int.MaxValue);
            foreach (View v in views)
            {
                int top = y;
                y += PadV;
                contentW = Math.Max(contentW, TextRenderer.MeasureText(v.Category.ToUpperInvariant(), fCat, big, Flags).Width);
                y += fCat.Height + Gap;
                contentW = Math.Max(contentW, TextRenderer.MeasureText(v.Title, fTitle, big, Flags).Width);
                y += fTitle.Height + Gap;
                if (v.Valid)
                {
                    string[] parts = Core.SplitMac(v.Hex, v.PrefixDigits);
                    contentW = Math.Max(contentW, TextRenderer.MeasureText(parts[0], fMonoBold, big, Flags).Width + TextRenderer.MeasureText(parts[1], fMono, big, Flags).Width);
                    y += fMono.Height + Gap;
                }
                if (!string.IsNullOrEmpty(v.Detail))
                {
                    contentW = Math.Max(contentW, TextRenderer.MeasureText(v.Detail, fSmall, big, Flags).Width);
                    y += fSmall.Height;
                }
                y += PadV;
                EntryRects.Add(new Rectangle(0, top, 0, y - top));
            }
            if (!string.IsNullOrEmpty(footer))
            {
                contentW = Math.Max(contentW, TextRenderer.MeasureText(footer, fSmall, big, Flags).Width);
                y += fSmall.Height + PadV * 2;
            }
            int width = fill ? maxWidth : Math.Min(Stripe + Pad + contentW + Pad, maxWidth);
            for (int i = 0; i < EntryRects.Count; i++) EntryRects[i] = new Rectangle(0, EntryRects[i].Y, width, EntryRects[i].Height);
            Size = new Size(width, y);
        }

        public void Paint(Graphics g, Palette pal, int hotEntry)
        {
            g.Clear(pal.Surface);
            int x = Stripe + Pad, w = Size.Width - x - Pad;
            for (int i = 0; i < Views.Count; i++)
            {
                View v = Views[i];
                Rectangle r = EntryRects[i];
                if (i == hotEntry) using (var b = new SolidBrush(pal.Line)) g.FillRectangle(b, r);
                using (var b = new SolidBrush(pal.Kind(v.Kind))) g.FillRectangle(b, 0, r.Top, Stripe, r.Height);
                if (i > 0) using (var pen = new Pen(pal.Line)) g.DrawLine(pen, Stripe, r.Top, r.Right, r.Top);
                int y = r.Top + PadV;
                TextRenderer.DrawText(g, v.Category.ToUpperInvariant(), fCat, new Rectangle(x, y, w, fCat.Height), pal.Kind(v.Kind), Flags);
                y += fCat.Height + Gap;
                TextRenderer.DrawText(g, v.Title, fTitle, new Rectangle(x, y, w, fTitle.Height), pal.Ink, Flags);
                y += fTitle.Height + Gap;
                if (v.Valid)
                {
                    string[] parts = Core.SplitMac(v.Hex, v.PrefixDigits);
                    int pw = TextRenderer.MeasureText(g, parts[0], fMonoBold, new Size(int.MaxValue, int.MaxValue), Flags).Width;
                    TextRenderer.DrawText(g, parts[0], fMonoBold, new Point(x, y), pal.Ink, Flags);
                    TextRenderer.DrawText(g, parts[1], fMono, new Point(x + pw, y), pal.Muted, Flags);
                    y += fMono.Height + Gap;
                }
                if (!string.IsNullOrEmpty(v.Detail))
                    TextRenderer.DrawText(g, v.Detail, fSmall, new Rectangle(x, y, w, fSmall.Height), pal.Muted, Flags);
            }
            if (!string.IsNullOrEmpty(Footer))
            {
                int fy = Size.Height - fSmall.Height - PadV;
                using (var pen = new Pen(pal.Line)) g.DrawLine(pen, 0, fy - PadV, Size.Width, fy - PadV);
                TextRenderer.DrawText(g, Footer, fSmall, new Rectangle(x, fy, w, fSmall.Height), pal.Muted, Flags);
            }
            using (var pen = new Pen(pal.Line)) g.DrawRectangle(pen, 0, 0, Size.Width - 1, Size.Height - 1);
        }

        public int EntryAt(Point p)
        {
            for (int i = 0; i < EntryRects.Count; i++) if (EntryRects[i].Contains(p)) return i;
            return -1;
        }

        public void Dispose()
        {
            fCat.Dispose(); fTitle.Dispose(); fMono.Dispose(); fMonoBold.Dispose(); fSmall.Dispose();
        }
    }

    /// <summary>Floating card near the cursor. Never takes the focus; a click copies "MAC<TAB>vendor".</summary>
    sealed class CardForm : Form
    {
        CardLayout layout;
        Palette pal = Palette.Current();
        int hot = -1;
        public string Source = "";
        readonly Action<string> copy;

        public CardForm(Action<string> copy)
        {
            this.copy = copy;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            Text = "MAC Inspector card";
            Cursor = Cursors.Hand;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000008 | 0x00000080 | 0x08000000; // TOPMOST | TOOLWINDOW | NOACTIVATE
                cp.ClassStyle |= 0x00020000;                          // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 3; // DWMWCP_ROUNDSMALL (Windows 11)
            try { Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        /// <summary>Shows the card below the anchor rectangle (or above it when there is no room).</summary>
        public void ShowCard(List<View> views, Rectangle anchor, string footer, string source)
        {
            Point cursor = Native.Cursor();
            float scale = Native.ScaleAt(cursor);
            Rectangle work = Native.WorkAreaAt(cursor);
            if (layout != null) layout.Dispose();
            pal = Palette.Current();
            layout = new CardLayout(views, footer, scale, Math.Min((int)(480 * scale), work.Width - 16));
            Source = source; hot = -1;

            int gap = (int)(8 * scale);
            int x = Math.Max(work.Left + 4, Math.Min(cursor.X - (int)(14 * scale), work.Right - layout.Size.Width - 4));
            int y = anchor.Bottom + gap;
            if (y + layout.Size.Height > work.Bottom) y = anchor.Top - gap - layout.Size.Height;
            y = Math.Max(work.Top + 4, y);

            if (!Visible) Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            Native.SetWindowPos(Handle, new IntPtr(-1), x, y, layout.Size.Width, layout.Size.Height, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
            Invalidate();
        }

        public void HideCard()
        {
            if (Visible) Hide();
            Source = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (layout != null) layout.Paint(e.Graphics, pal, hot);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = layout == null ? -1 : layout.EntryAt(e.Location);
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (layout == null) return;
            int i = layout.EntryAt(e.Location);
            if (i < 0 || !layout.Views[i].Valid) return;
            View v = layout.Views[i];
            copy(Core.FormatHex(v.Hex) + "\t" + v.Title);
            Point anchor = new Point(Left, Top);
            ShowCard(new List<View> { v }, new Rectangle(anchor.X, anchor.Y - 8, 1, 1), Texts.T("card.copied"), Source);
            var t = new System.Windows.Forms.Timer { Interval = 900 };
            t.Tick += delegate { t.Stop(); t.Dispose(); HideCard(); };
            t.Start();
        }
    }

    // ─── Search window ────────────────────────────────────────────────────────

    /// <summary>One card inside the search window (system DPI, scaled by Windows on other monitors).</summary>
    sealed class CardControl : Control
    {
        readonly View view;
        readonly Action<string> copy;
        CardLayout layout;
        bool hover;
        string footer;

        public CardControl(View v, Action<string> copy)
        {
            view = v; this.copy = copy;
            DoubleBuffered = true;
            Cursor = v.Valid ? Cursors.Hand : Cursors.Default;
            Margin = new Padding(0, 0, 0, 8);
        }

        public void Fit(int width)
        {
            if (layout != null) layout.Dispose();
            float scale = DeviceDpiScale();
            layout = new CardLayout(new List<View> { view }, footer, scale, width, true);
            Size = new Size(width, layout.Size.Height);
            Invalidate();
        }

        float DeviceDpiScale()
        {
            using (Graphics g = CreateGraphics()) return g.DpiX / 96f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (layout == null) return;
            Palette pal = Palette.Current();
            layout.Paint(e.Graphics, pal, hover && view.Valid ? 0 : -1);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!view.Valid) return;
            copy(Core.FormatHex(view.Hex) + "\t" + view.Title);
            footer = Texts.T("card.copied"); Fit(Width);
            var t = new System.Windows.Forms.Timer { Interval = 1100 };
            t.Tick += delegate { t.Stop(); t.Dispose(); footer = null; if (!IsDisposed) Fit(Width); };
            t.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && layout != null) layout.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class SearchForm : Form
    {
        readonly App app;
        readonly TextBox input;
        readonly Label label, status, footer;
        readonly LinkLabel credit;
        readonly FlowLayoutPanel list;

        public SearchForm(App app)
        {
            this.app = app;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "MAC Inspector";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(440, 540);
            MinimumSize = new Size(360, 360);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            if (app.AppIcon != null) Icon = app.AppIcon;

            label = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26, Padding = new Padding(12, 8, 12, 0) };
            input = new TextBox { Dock = DockStyle.Top, Multiline = true, Height = 64, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5f), AcceptsReturn = true };
            var inputHost = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(12, 4, 12, 8) };
            inputHost.Controls.Add(input);
            status = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 24, Padding = new Padding(12, 2, 12, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 4, 12, 4) };
            footer = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 24, Padding = new Padding(12, 6, 12, 0), Font = new Font("Segoe UI", 8.5f) };
            // Copyright line with a link to the license and credits, as in the extension (license, section 3 f)
            credit = new LinkLabel { Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(12, 2, 12, 6), Font = new Font("Segoe UI", 8.5f), LinkBehavior = LinkBehavior.HoverUnderline };
            credit.LinkClicked += delegate { app.ShowAbout(); };

            Controls.Add(list);
            Controls.Add(status);
            Controls.Add(inputHost);
            Controls.Add(label);
            Controls.Add(footer);
            Controls.Add(credit);

            input.TextChanged += delegate { Render(); };
            list.Resize += delegate { FitCards(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
            ResumeLayout(false);
            ApplyTexts();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        // Dark title bar when Windows uses the dark app theme (DWMWA_USE_IMMERSIVE_DARK_MODE)
        void ApplyTitleBar()
        {
            bool isDark = Palette.Current().Dark;
            int dark = isDark ? 1 : 0;
            try
            {
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                // Dark scroll bars for the list and the text box
                Native.SetWindowTheme(list.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
                Native.SetWindowTheme(input.Handle, isDark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        public void ApplyTexts()
        {
            Palette pal = Palette.Current();
            if (IsHandleCreated) ApplyTitleBar();
            BackColor = pal.Back; ForeColor = pal.Ink;
            input.BackColor = pal.Surface; input.ForeColor = pal.Ink;
            label.ForeColor = pal.Muted; status.ForeColor = pal.Ink; footer.ForeColor = pal.Muted;
            label.Text = Texts.T("search.label");
            footer.Text = Texts.T("search.footer", "date", app.DataDate);
            string copyright = "© 2026 CatalaniDev", linkText = Texts.T("tray.about");
            credit.Text = copyright + "  ·  " + linkText;
            credit.LinkArea = new LinkArea(credit.Text.Length - linkText.Length, linkText.Length);
            credit.ForeColor = pal.Muted; credit.LinkColor = pal.Kind("vendor"); credit.ActiveLinkColor = pal.Kind("vendor"); credit.VisitedLinkColor = pal.Kind("vendor");
            Render();
        }

        public void ShowWith(string text, string message)
        {
            if (text != null) input.Text = text;
            Render();
            if (message != null) status.Text = message;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Native.SetForegroundWindow(Handle);
            Activate();
            input.Focus();
            input.SelectAll();
            app.Log("search-open", "count", list.Controls.Count);
        }

        // Child windows must share the DPI mode of this window (system DPI): Render can be called from a
        // per-monitor context (hotkey, second start), so the cards are always created in the right one.
        void Render()
        {
            Native.WithDpi(Native.DpiSystemAware, () => { RenderCards(); return 0; });
        }

        void RenderCards()
        {
            list.SuspendLayout();
            var old = new Control[list.Controls.Count];
            list.Controls.CopyTo(old, 0);
            list.Controls.Clear();
            foreach (Control c in old) c.Dispose();
            string raw = input.Text.Trim();
            var views = new List<View>();
            if (raw.Length == 0)
            {
                foreach (string hex in app.Recent) views.Add(app.ViewFor(hex));
                status.Text = views.Count > 0 ? Texts.T("search.recent") : Texts.T("search.empty");
            }
            else
            {
                var seen = new HashSet<string>();
                foreach (MacMatch m in Core.FindMacs(raw))
                {
                    string hex = Core.ToHex(m.Mac);
                    if (seen.Add(hex) && views.Count < 100) views.Add(app.ViewFor(hex));
                }
                if (views.Count == 0)
                {
                    // A bare prefix (6-12 hex digits) is looked up too, like in the extension popup
                    string hex = Core.ToHex(raw);
                    if (raw.Length <= 64 && Core.IsHexPrefix(hex)) views.Add(app.ViewFor(hex));
                    else views.Add(Core.Describe(new LookupResult { Valid = false }));
                    status.Text = views[0].Valid ? Texts.T("search.one") : Texts.T("search.none");
                }
                else
                {
                    status.Text = views.Count == 1 ? Texts.T("search.one") : Texts.T("search.count", "count", views.Count);
                    foreach (View v in views) app.Remember(v.Hex);
                }
                app.Log("search", "count", views.Count);
            }
            foreach (View v in views) list.Controls.Add(new CardControl(v, app.Copy));
            FitCards();
            list.ResumeLayout();
        }

        void FitCards()
        {
            int w = list.ClientSize.Width - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
            foreach (Control c in list.Controls) { var card = c as CardControl; if (card != null) card.Fit(Math.Max(200, w)); }
        }
    }

    // ─── Application ──────────────────────────────────────────────────────────

    /// <summary>Hidden message-only window: global hotkey, clipboard updates, second-instance requests.</summary>
    sealed class HostWindow : NativeWindow
    {
        public const string Caption = "MacInspectorDesktop.Host";
        public const int WM_HOTKEY = 0x0312, WM_CLIPBOARDUPDATE = 0x031D, WM_SHOWSEARCH = 0x8001, WM_EXITAPP = 0x8002;
        readonly Action<int> handler;

        public HostWindow(Action<int> handler)
        {
            this.handler = handler;
            CreateHandle(new CreateParams { Caption = Caption, Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY || m.Msg == WM_CLIPBOARDUPDATE || m.Msg == WM_SHOWSEARCH || m.Msg == WM_EXITAPP) handler(m.Msg);
            base.WndProc(ref m);
        }
    }

    public sealed class App : ApplicationContext
    {
        public const string Version = "1.0.0";
        public const string Homepage = "https://github.com/catalani-dev/mac-inspector-windows";
        readonly string dir;
        readonly Settings settings = Settings.Load();
        readonly OuiRegistry registry;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly CardForm card;
        readonly HostWindow host;
        readonly HoverEngine hover;
        readonly System.Windows.Forms.Timer autoHide;
        readonly string logPath = Environment.GetEnvironmentVariable("MACI_TEST_LOG");
        SearchForm search;
        DateTime ignoreClipboardUntil = DateTime.MinValue;
        Point cardShownAt;
        DateTime cardShownTime;
        bool hotkeyOk;
        public readonly Icon AppIcon;
        public readonly List<string> Recent = new List<string>();
        public string DataDate = "";

        public App(string dir)
        {
            this.dir = dir;
            Texts.Lang = Texts.Resolve(settings.Lang);
            registry = OuiRegistry.Load(Path.Combine(dir, "oui-data.json"));
            DateTime gen;
            if (DateTime.TryParse(registry.Generated, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out gen)) DataDate = gen.ToString("yyyy-MM-dd");

            string icoPath = Path.Combine(dir, "mac-inspector.ico");
            if (File.Exists(icoPath)) AppIcon = new Icon(icoPath);

            card = new CardForm(Copy);
            card.CreateControl();
            var _ = card.Handle;

            menu = Native.WithDpi(Native.DpiSystemAware, () => { var m = new ContextMenuStrip(); var h = m.Handle; return m; });
            tray = new NotifyIcon { Icon = AppIcon ?? SystemIcons.Information, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSearch(null, null); };
            BuildMenu();

            host = new HostWindow(OnHostMessage);
            hotkeyOk = Native.RegisterHotKey(host.Handle, 1, 0x0001 | 0x0002 | 0x4000, (uint)Keys.M); // Alt+Ctrl, no repeat
            Native.AddClipboardFormatListener(host.Handle);

            autoHide = new System.Windows.Forms.Timer { Interval = 100 };
            autoHide.Tick += delegate { CheckAutoHide(); };
            autoHide.Start();

            hover = new HoverEngine(
                hit => card.BeginInvoke((Action)(() => ShowHover(hit))),
                () => card.BeginInvoke((Action)(() => { if (card.Source == "hover") HideCard(); })));
            hover.Enabled = settings.Hover;
            if (Environment.GetEnvironmentVariable("MACI_VERBOSE") != null) hover.OnProbe = info => Log("probe", "info", info);

            if (!settings.Welcomed)
            {
                tray.ShowBalloonTip(6000, "MAC Inspector", Texts.T("tray.started"), ToolTipIcon.Info);
                settings.Welcomed = true; settings.Save();
            }
            if (!hotkeyOk) tray.ShowBalloonTip(6000, "MAC Inspector", Texts.T("tray.hotkeyBusy"), ToolTipIcon.Warning);
            Log("ready", "hotkey", hotkeyOk, "prefixes", registry.Total, "lang", Texts.Lang);
        }

        // ── Lookups ──

        public View ViewFor(string mac)
        {
            return Core.Describe(registry.Lookup(mac));
        }

        public void Remember(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return;
            Recent.Remove(hex);
            Recent.Insert(0, hex);
            if (Recent.Count > 20) Recent.RemoveAt(Recent.Count - 1);
        }

        public void Copy(string text)
        {
            ignoreClipboardUntil = DateTime.UtcNow.AddMilliseconds(800);
            try { Clipboard.SetText(text); Log("copy", "text", text); } catch { }
        }

        // ── Cards ──

        void ShowHover(HoverHit hit)
        {
            if (!settings.Hover) return;
            View v = ViewFor(hit.Mac);
            Remember(v.Hex);
            card.ShowCard(new List<View> { v }, hit.Rect, null, "hover");
            hover.CardBounds = card.Bounds;
            Log("show", "source", "hover", "macs", new[] { hit.Mac }, "titles", new[] { v.Title }, "bounds", RectText(card.Bounds), "anchor", RectText(hit.Rect), "ms", hit.Ms);
        }

        void ShowList(List<MacMatch> found, string source)
        {
            var views = new List<View>();
            var seen = new HashSet<string>();
            foreach (MacMatch m in found)
            {
                string hex = Core.ToHex(m.Mac);
                if (!seen.Add(hex)) continue;
                if (views.Count < 3) views.Add(ViewFor(hex));
                Remember(hex);
            }
            int more = seen.Count - views.Count;
            string footer = more > 0 ? Texts.T("card.more", "count", more) : Texts.T("card.copy");
            Point c = Native.Cursor();
            card.ShowCard(views, new Rectangle(c.X, c.Y + (int)(12 * Native.ScaleAt(c)), 1, 1), footer, source);
            cardShownAt = c;
            cardShownTime = DateTime.UtcNow;
            hover.CardBounds = card.Bounds;
            var macs = new List<string>(); var titles = new List<string>();
            foreach (View v in views) { macs.Add(v.Hex); titles.Add(v.Title); }
            Log("show", "source", source, "macs", macs, "titles", titles, "more", more, "bounds", RectText(card.Bounds));
        }

        void HideCard()
        {
            if (!card.Visible) return;
            string src = card.Source;
            card.HideCard();
            hover.CardBounds = Rectangle.Empty;
            Log("hide", "source", src);
        }

        /// <summary>Cards opened by the clipboard or the hotkey close when the cursor moves away or after 12 s.</summary>
        void CheckAutoHide()
        {
            if (!card.Visible || card.Source == "hover") return;
            Point c = Native.Cursor();
            if (card.Bounds.Contains(c)) { cardShownTime = DateTime.UtcNow; return; }
            double dist = Math.Sqrt(Math.Pow(c.X - cardShownAt.X, 2) + Math.Pow(c.Y - cardShownAt.Y, 2));
            if (dist > 260 * Native.ScaleAt(c) || (DateTime.UtcNow - cardShownTime).TotalSeconds > 12) HideCard();
        }

        // ── Hotkey, clipboard, second instance ──

        void OnHostMessage(int msg)
        {
            if (msg == HostWindow.WM_HOTKEY) OnHotkey();
            else if (msg == HostWindow.WM_CLIPBOARDUPDATE) OnClipboard();
            else if (msg == HostWindow.WM_SHOWSEARCH) ShowSearch(null, null);
            else if (msg == HostWindow.WM_EXITAPP) ExitThread();
        }

        void OnClipboard()
        {
            if (!settings.Clipboard || DateTime.UtcNow < ignoreClipboardUntil) return;
            string text = ReadClipboard();
            if (text == null) return;
            var found = Core.FindMacs(text);
            if (found.Count > 0) ShowList(found, "clipboard");
        }

        /// <summary>Clipboard text, or null. Content marked as private by password managers is skipped.</summary>
        string ReadClipboard()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    IDataObject data = Clipboard.GetDataObject();
                    if (data == null) return null;
                    if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
                    {
                        Log("clipboard-skipped");
                        return null;
                    }
                    if (!data.GetDataPresent(DataFormats.UnicodeText)) return null;
                    string text = data.GetData(DataFormats.UnicodeText) as string;
                    return text != null && text.Length > 20000 ? text.Substring(0, 20000) : text;
                }
                catch (ExternalException) { Thread.Sleep(40); }
                catch { return null; }
            }
            return null;
        }

        void OnHotkey()
        {
            int ownPid = Process.GetCurrentProcess().Id;
            Task.Factory.StartNew(() => UiaText.Selection(ownPid)).ContinueWith(t =>
            {
                string sel = t.Result;
                Log("hotkey", "focus", UiaText.LastSelectionInfo, "selectionLength", sel == null ? -1 : sel.Length);
                var found = Core.FindMacs(sel);
                string source = "selection";
                if (found.Count == 0)
                {
                    sel = ReadClipboard();
                    found = Core.FindMacs(sel);
                    source = "hotkey-clipboard";
                }
                if (found.Count > 0) ShowList(found, source);
                else ShowSearch(null, Texts.T("search.nothingFound"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowSearch(string text, string message)
        {
            if (search == null || search.IsDisposed)
                search = Native.WithDpi(Native.DpiSystemAware, () => { var f = new SearchForm(this); var h = f.Handle; return f; }); // handle created now, as a system-DPI window
            HideCard();
            search.ShowWith(text, message);
        }

        // ── Menu ──

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.Items.Add(Texts.T("tray.search"), null, delegate { ShowSearch(null, null); }).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            var hoverItem = new ToolStripMenuItem(Texts.T("tray.hover")) { Checked = settings.Hover, CheckOnClick = true };
            hoverItem.CheckedChanged += delegate { settings.Hover = hoverItem.Checked; hover.Enabled = settings.Hover; settings.Save(); if (!settings.Hover) HideCard(); };
            var clipItem = new ToolStripMenuItem(Texts.T("tray.clipboard")) { Checked = settings.Clipboard, CheckOnClick = true };
            clipItem.CheckedChanged += delegate { settings.Clipboard = clipItem.Checked; settings.Save(); };
            menu.Items.Add(hoverItem);
            menu.Items.Add(clipItem);
            var lang = new ToolStripMenuItem(Texts.T("tray.language"));
            foreach (var opt in new[] { new[] { "auto", Texts.T("tray.langAuto") }, new[] { "en", "English" }, new[] { "it", "Italiano" } })
            {
                string value = opt[0];
                var item = new ToolStripMenuItem(opt[1]) { Checked = settings.Lang == value };
                item.Click += delegate { SetLanguage(value); };
                lang.DropDownItems.Add(item);
            }
            menu.Items.Add(lang);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Texts.T("tray.about"), null, delegate { ShowAbout(); });
            menu.Items.Add(Texts.T("tray.exit"), null, delegate { ExitThread(); });
            string tip = Texts.T("tray.tooltip");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void SetLanguage(string value)
        {
            settings.Lang = value; settings.Save();
            Texts.Lang = Texts.Resolve(value);
            BuildMenu();
            if (search != null && !search.IsDisposed) search.ApplyTexts();
            Log("lang", "lang", Texts.Lang);
        }

        public void ShowAbout()
        {
            // Always in English, like the About page of the extension
            string text = "MAC Inspector for Windows " + Version + "\n\n" +
                          "© 2026 CatalaniDev <catalanidev@gmail.com>\n" +
                          "Released under the MAC Inspector License 1.0 (see LICENSE).\n\n" +
                          "Data: IEEE Registration Authority, " + DataDate + " (" + registry.Total.ToString("N0", CultureInfo.InvariantCulture) + " prefixes).\n" +
                          "Vendor names are trademarks of their respective owners.\n\n" +
                          "Everything runs on this computer: nothing is sent anywhere,\n" +
                          "and password fields are never read.\n\n" + Homepage;
            Native.WithDpi(Native.DpiSystemAware, () => MessageBox.Show(text, "About MAC Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information));
        }

        protected override void ExitThreadCore()
        {
            hover.Stop();
            try { Native.UnregisterHotKey(host.Handle, 1); Native.RemoveClipboardFormatListener(host.Handle); } catch { }
            tray.Visible = false;
            tray.Dispose();
            autoHide.Stop();
            card.Close();
            if (search != null) search.Dispose();
            host.DestroyHandle();
            Log("exit");
            base.ExitThreadCore();
        }

        // ── Test log (only when MACI_TEST_LOG is set) ──

        static string RectText(Rectangle r) { return r.X + "," + r.Y + "," + r.Width + "," + r.Height; }

        public void Log(string evt, params object[] kv)
        {
            if (string.IsNullOrEmpty(logPath)) return;
            try
            {
                var d = new Dictionary<string, object> { { "t", DateTime.Now.ToString("HH:mm:ss.fff") }, { "event", evt } };
                for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
                File.AppendAllText(logPath, new JavaScriptSerializer().Serialize(d) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    public static class Program
    {
        /// <summary>Asks the running instance to quit. Returns false when none is running.</summary>
        public static bool Exit()
        {
            IntPtr other = Native.FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, HostWindow.Caption);
            return other != IntPtr.Zero && Native.PostMessage(other, HostWindow.WM_EXITAPP, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Entry point called by MAC-Inspector.ps1.</summary>
        public static void Run(string dir)
        {
            // Per-monitor DPI for the whole process: cards are placed in physical pixels next to the
            // text found by UI Automation, and sized for the monitor they appear on. A thread-level
            // setting is not enough: Windows Forms switches back to the process setting for its windows.
            try { Native.SetProcessDpiAwarenessContext(Native.DpiPerMonitorV2); } catch { }
            try { Native.SetThreadDpiAwarenessContext(Native.DpiPerMonitorV2); } catch { }

            bool created;
            using (var mutex = new Mutex(true, @"Local\MacInspectorDesktop", out created))
            {
                if (!created)
                {
                    // Already running: ask the running instance to open its search window
                    IntPtr other = Native.FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, HostWindow.Caption);
                    if (other != IntPtr.Zero)
                    {
                        // This process was started by the user and may bring a window to the front: pass the right on
                        uint pid; Native.GetWindowThreadProcessId(other, out pid);
                        Native.AllowSetForegroundWindow(pid);
                        Native.PostMessage(other, HostWindow.WM_SHOWSEARCH, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }
                IntPtr console = Native.GetConsoleWindow();
                if (console != IntPtr.Zero && Environment.GetEnvironmentVariable("MACI_KEEP_CONSOLE") == null) Native.ShowWindow(console, 0);
                try { Application.EnableVisualStyles(); } catch { }
                Application.Run(new App(dir));
            }
        }
    }
}
