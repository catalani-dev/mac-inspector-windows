<p align="center">
  <img src="docs/img/logo.svg" width="112" height="112" alt="Logo di MAC Inspector">
</p>

<h1 align="center">MAC Inspector per Windows</h1>

<p align="center">
  <a href="README.md">English</a> · <strong>Italiano</strong>
</p>

<p align="center">
  <strong>Passa il cursore su un MAC address in qualsiasi programma e scopri chi ha prodotto il dispositivo.</strong><br>
  Console, terminali, Blocco note, Office, browser o qualsiasi testo copiato: il produttore compare accanto al cursore.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/versione-1.0.0-2F6FDF" alt="Versione 1.0.0">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-2F6FDF" alt="Windows 10 e 11">
  <img src="https://img.shields.io/badge/PowerShell-5.1-2F6FDF" alt="Windows PowerShell 5.1">
  <img src="https://img.shields.io/badge/licenza-MAC%20Inspector%201.0-1D2127" alt="MAC Inspector License 1.0">
</p>

![Scheda mostrata su un MAC address in una console](docs/img/hover.png)

MAC Inspector per Windows porta l'[estensione per Chrome MAC Inspector](https://github.com/catalani-dev/mac-inspector) su tutto il desktop. Usa lo stesso registro IEEE e dà gli stessi risultati.

## Caratteristiche principali

- **Il produttore al passaggio del cursore.** Fermando il mouse su un MAC address compare una scheda con il produttore e il prefisso del produttore in grassetto. Funziona nei programmi che espongono il loro testo all'accessibilità di Windows (vedi [Dove funziona il passaggio del cursore](#dove-funziona-il-passaggio-del-cursore)).
- **Testo copiato.** Copiando un testo che contiene MAC address, in qualsiasi programma, compare una scheda con l'elenco. Serve per client SSH, desktop remoti ed editor dove il passaggio del cursore non è possibile.
- **Ctrl+Alt+M.** Identifica il testo selezionato oppure, se non c'è una selezione, gli appunti.
- **Finestra di ricerca.** Incolla righe di log, tabelle ARP o un prefisso come `B8-27-EB`: compaiono tutti gli indirizzi trovati. Un clic copia MAC e produttore.
- **Registro IEEE completo.** Blocchi MA-L, MA-M, MA-S, IAB e CID, con ricerca del prefisso più lungo.
- **Tipi di indirizzo a colori.**

  | Colore | Tipo di indirizzo |
  |---|---|
  | Blu | Produttore registrato |
  | Arancione | MAC casuale o virtuale (amministrato localmente) |
  | Viola | Indirizzo di gruppo o speciale |
  | Grigio | Non registrato |

- **Indirizzi speciali.** Broadcast, multicast IPv4 e IPv6, STP, LACP, LLDP, CDP, PVST+, VRRP e HSRP.
- **Notazioni.** `00:1C:0E:11:22:33`, `00-1C-0E-11-22-33`, `001c.0e11.2233` (Cisco), `001c-0e11-2233` (Huawei / H3C), `001c0e-112233` (HP / Aruba), `001C0E112233`.
- **Italiano e inglese**, tema chiaro e scuro come Windows, nitido anche su schermi ad alta risoluzione e con più monitor.
- **Niente da installare.** Una cartella, avviata da uno script; usa solo componenti già presenti in Windows.

| Testo copiato | Finestra di ricerca |
|---|---|
| ![Scheda con i MAC address di una tabella ARP copiata](docs/img/clipboard.png) | ![Finestra di ricerca con tre indirizzi trovati nel testo incollato](docs/img/search.png) |

## Dove funziona il passaggio del cursore

Il programma legge il testo sotto il cursore con UI Automation, l'interfaccia di Windows usata dai lettori di schermo. Misure su Windows 11, con un punto di controllo accanto a ogni indirizzo (sull'IP della stessa riga) per verificare che non compaiano risultati sbagliati:

| Programma | Indirizzi riconosciuti | Risultati sbagliati |
|---|---|---|
| Prompt dei comandi (console classica) | 6 su 6 | 0 |
| Windows Terminal | 6 su 6 | 0 |
| Blocco note | 6 su 6 | 0 |
| Microsoft Word | 6 su 6 | 0 |
| Microsoft Excel | 3 su 3 | 0 |
| Google Chrome (pagine web) | 4 su 4 | 0 |

Ogni lettura ha richiesto meno di 100 ms (tra 10 e 82 ms).

I programmi che disegnano il testo da soli senza esporlo, come l'editor di VS Code, gran parte dei client SSH (PuTTY e simili) e le sessioni di desktop remoto, non si possono leggere al passaggio del cursore. Lì basta copiare il testo oppure premere **Ctrl+Alt+M** sulla selezione. Il riconoscimento ottico del testo sullo schermo (OCR) non viene usato di proposito: nelle prove l'OCR integrato in Windows ha letto correttamente 0 MAC su 10, confondendo `0` con `e` e spezzando gli indirizzi ai due punti.

## Installazione

1. **Scarica.** Apri l'[ultima release](https://github.com/catalani-dev/mac-inspector-windows/releases/latest) e scarica `mac-inspector-windows-<versione>.zip` dalla sezione **Assets**.
2. **Sblocca.** Clic destro sullo ZIP, **Proprietà**, spunta **Sblocca** e **OK**. Windows contrassegna i file scaricati: sbloccare lo ZIP prima di estrarlo evita gli avvisi di sicurezza su ogni file.
3. **Estrai** in una cartella definitiva, per esempio `Documenti\mac-inspector-windows`.
4. **Avvia** con un doppio clic su `Start-MAC-Inspector.cmd`. L'icona con la lente compare nell'area di notifica, vicino all'orologio; al primo avvio compare una breve notifica di benvenuto.

Richiede Windows 10 o 11 con Windows PowerShell 5.1 e .NET Framework 4.x, già presenti in Windows. Non servono permessi di amministratore.

Il programma parte dal suo sorgente leggibile: `MAC-Inspector.ps1` compila `MacInspector.cs` con il compilatore C# incluso in Windows (circa un secondo) e lo avvia. Non viene installato niente e non si scaricano eseguibili.

**Avvio con Windows.** Premi **Win+R**, scrivi `shell:startup` e premi Invio, poi crea in quella cartella un collegamento a `Start-MAC-Inspector.cmd`.

**Aggiornamento.** Scegli **Esci** dal menu dell'icona, sostituisci i file della cartella con quelli della nuova release e riavvialo. Le impostazioni restano.

**Rimozione.** Scegli **Esci** dal menu dell'icona e cancella la cartella. Le impostazioni sono in `%APPDATA%\MAC Inspector Desktop`, che si può cancellare a sua volta.

## Come funziona

1. **Passa il cursore.** Ferma il mouse un istante su un MAC address. Sotto compare una scheda con il tipo di indirizzo, il produttore, l'indirizzo con il prefisso del produttore in grassetto e il blocco del registro corrispondente. Spostando il mouse, la scheda sparisce.
2. **Copia.** Copia un testo che contiene MAC address. Una scheda ne mostra fino a tre; gli altri vengono contati e **Ctrl+Alt+M** apre l'elenco completo.
3. **Seleziona e premi Ctrl+Alt+M.** Viene letto il testo selezionato; se non c'è una selezione si usano gli appunti. Se non ci sono MAC address, si apre la finestra di ricerca.
4. **Cerca.** Clicca sull'icona, incolla il testo e leggi i risultati. Un clic su una scheda copia `MAC<TAB>produttore`.

Non viene inviato niente a nessuno: la ricerca usa la copia del registro IEEE nella cartella del programma.

## Uso

Clic destro sull'icona nell'area di notifica:

| Voce del menu | Effetto |
|---|---|
| **Cerca…** | Apre la finestra di ricerca (anche con un clic sinistro sull'icona) |
| **Mostra il vendor al passaggio del cursore** | Attiva o disattiva le schede al passaggio del cursore |
| **Riconosci i MAC copiati** | Attiva o disattiva le schede per il testo copiato |
| **Lingua** | Automatica (come Windows), English o Italiano |
| **Licenza e crediti** | Versione, copyright, licenza e fonte dei dati |
| **Esci** | Chiude MAC Inspector |

| Scorciatoia | Azione |
|---|---|
| `Ctrl+Alt+M` | Identifica la selezione o gli appunti |
| `Esc` | Chiude la finestra di ricerca |

Se Ctrl+Alt+M è già usato da un altro programma, MAC Inspector lo segnala all'avvio e la finestra di ricerca resta disponibile dall'icona.

Avviando di nuovo `Start-MAC-Inspector.cmd` mentre MAC Inspector è in esecuzione si apre la finestra di ricerca. `MAC-Inspector.ps1 -Exit` chiude l'istanza in esecuzione, per script e aggiornamenti.

## Privacy e sicurezza

- **Tutto avviene in locale.** Nessuna connessione di rete, nessuna statistica, nessun servizio esterno.
- **Sola lettura.** MAC Inspector legge il testo tramite l'interfaccia di accessibilità e gli appunti; non scrive, non clicca e non modifica niente negli altri programmi.
- **I campi password non vengono mai letti**, né al passaggio del cursore né con Ctrl+Alt+M.
- **I contenuti privati degli appunti vengono ignorati.** I gestori di password contrassegnano i segreti copiati come esclusi dal monitoraggio degli appunti (`ExcludeClipboardContentFromMonitorProcessing`): quei contenuti non vengono letti.
- **Non viene salvato niente** a parte il file delle impostazioni. Il testo viene solo esaminato in memoria per cercare MAC address; l'elenco degli indirizzi recenti si perde alla chiusura del programma. Le schede per il testo copiato si possono disattivare dal menu.
- **Nessun permesso di amministratore e nessuna installazione.** Il sorgente è testo leggibile prima di eseguirlo e viene compilato sul tuo computer.
- **Criteri di esecuzione.** Lo script di avvio usa PowerShell con `-ExecutionPolicy Bypass` solo per quel processo. L'impostazione di sistema non viene modificata.

## Aggiornare il registro IEEE

`oui-data.json` è lo stesso file usato dall'[estensione per Chrome](https://github.com/catalani-dev/mac-inspector), generato dal suo `build-oui-database.py` a partire dai registri IEEE. Per aggiornarlo, copia in questa cartella il file `oui-data.json` più recente di quel repository (oppure generalo con lo script), poi chiudi e riavvia MAC Inspector. La data dei dati compare in fondo alla finestra di ricerca.

## Sviluppo

| File | Scopo |
|---|---|
| `MacInspector.cs` | Il programma: riconoscimento dei MAC, ricerca nel registro, lettura con UI Automation, schede, finestra di ricerca, menu dell'icona |
| `MAC-Inspector.ps1` | Compila `MacInspector.cs` e lo avvia; `-Exit` chiude l'istanza in esecuzione |
| `Start-MAC-Inspector.cmd` | Avvia il programma senza finestra della console |
| `oui-data.json` | Dati del registro IEEE (dati di terze parti, vedere [NOTICE.md](NOTICE.md)) |
| `mac-inspector.ico` | Icona del programma |
| `tests/` | Test automatici |
| `docs/img/` | Logo, schermate e immagine di anteprima usate su GitHub |

Per eseguire i test:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Test-Core.ps1
```

`tests/vectors.json` contiene i risultati di `mac-core.js` e `i18n.js` dell'estensione per Chrome sugli stessi testi e sul piccolo registro di `tests/fixture-oui.json`. Il test verifica che riconoscimento, ricerche e testi in italiano e in inglese siano identici e che il registro incluso si carichi correttamente.

Impostando la variabile d'ambiente `MACI_TEST_LOG` con il percorso di un file, il programma vi scrive i suoi eventi (schede mostrate e nascoste, ricerche, scorciatoia), per i test end-to-end.

Ogni file sorgente inizia con un'intestazione [SPDX](https://spdx.dev/) che indica il titolare del copyright e la licenza.

## Licenza

Copyright (c) 2026 CatalaniDev. Distribuito con la **MAC Inspector License 1.0**, la stessa dell'estensione per Chrome. È una licenza a sorgente visibile, non una licenza open source. Il testo completo è in [LICENSE](LICENSE), in inglese, che è la versione che fa fede. Questa sintesi in italiano serve solo a orientarsi.

| | |
|---|---|
| **Consentito** | Leggere il codice; usare MAC Inspector per sé o all'interno della propria organizzazione, anche al lavoro; modificarlo per uso proprio. |
| **Consentito con crediti** | Includere MAC Inspector, modificato o no, in un proprio strumento **gratuito e non commerciale**, purché quello strumento aggiunga funzionalità sostanziali e i crediti vengano mantenuti (vedi sotto). |
| **Richiede autorizzazione scritta** | Includere MAC Inspector in un prodotto o servizio **commerciale** (a pagamento, in abbonamento, con versioni a pagamento, servizi online a pagamento, in pacchetto con offerte a pagamento o finanziati dalla pubblicità); ridistribuirlo da solo; pubblicarlo o pubblicarne una copia su uno store o un canale di distribuzione; venderlo da solo; rimuovere le indicazioni di copyright. |

### Includere MAC Inspector nel proprio strumento

**Prodotti commerciali.** Se il tuo prodotto o servizio genera guadagno in qualunque forma, scrivi a <catalanidev@gmail.com> prima di includere MAC Inspector. L'autorizzazione viene valutata caso per caso.

**Strumenti gratuiti e non commerciali.** Segui questi passi:

1. Mantieni l'intestazione SPDX in ogni file che riutilizzi.
2. Distribuisci una copia di [LICENSE](LICENSE) oppure un collegamento al file.
3. Inserisci i crediti nella documentazione e nella sezione "Informazioni" o "Crediti":

   ```text
   Includes MAC Inspector, Copyright (c) 2026 CatalaniDev,
   used under the MAC Inspector License 1.0.
   ```

4. Dichiara se hai modificato il codice.
5. Non chiamare il tuo strumento "MAC Inspector" e non lasciare intendere che sia approvato dall'autore.
6. Se riutilizzi la finestra di ricerca o la finestra "About", lascia visibili la riga "© 2026 CatalaniDev" e il collegamento "Licenza e crediti".

I dati del registro IEEE e i marchi citati appartengono ai rispettivi proprietari e non sono coperti da questa licenza: vedere [NOTICE.md](NOTICE.md). MAC Inspector non è affiliato a IEEE, Microsoft o Google, né da loro approvato.

## Autore

**CatalaniDev**, <catalanidev@gmail.com>

Per autorizzazioni non previste dalla licenza, commenti o segnalazioni di problemi, scrivi all'indirizzo qui sopra.
