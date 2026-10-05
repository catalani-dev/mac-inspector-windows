# Changelog

All notable changes to MAC Inspector for Windows are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## 1.0.0

First release.

### Added
- Vendor card when the mouse rests on a MAC address, in applications that expose their text through Windows UI Automation (consoles, Windows Terminal, Notepad, Word, Excel, browsers).
- Card for MAC addresses in copied text, from any application. Clipboard content marked as private by password managers is skipped.
- Ctrl+Alt+M: identifies the selected text, or the clipboard when nothing is selected.
- Search window for pasted addresses, log lines and bare prefixes; one click copies MAC and vendor.
- Icon in the notification area with the settings: hover, copied text, language (Automatic, English, Italiano), license and credits.
- Same detection, IEEE registry lookup and texts as the MAC Inspector Chrome extension 3.4.1, checked by automated tests.
- Light and dark theme following Windows; per-monitor DPI for the cards.
- `-Exit` option to close the running instance; a second start opens the search window.
