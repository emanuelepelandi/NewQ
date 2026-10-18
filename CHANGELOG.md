# NewQ - Changelog

Storico delle modifiche a NewQ. La prima parte descrive le funzioni presenti all'inizio del changelog (commit `74ce982`, 2026-10-11); da lì in poi ogni modifica viene aggiunta in coda, nella sezione *Modifiche*, dalla più vecchia alla più recente.

Formato di ogni voce:

```
### AAAA-MM-GG - Titolo breve (commit `abcdef0`)
- **Aggiunto** / **Cambiato** / **Corretto** / **Rimosso**: cosa, dal punto di vista di chi usa NewQ.
```

---

## Stato di partenza (2026-10-11)

NewQ è uno show control per Windows ispirato a QLab: una lista di cue lanciate con **GO** che pilotano audio, video, immagini, sorgenti dal vivo, MIDI e dispositivi in rete. Interfaccia in italiano, tema scuro (ModernWpfUI), uscite video su GPU (Direct3D 11).

### Tipi di cue

| Cue | Funzioni |
|---|---|
| **Audio** | WAV, MP3, AIFF, FLAC, M4A/AAC, WMA. Volume in dB, inizio/fine (trim), loop, fade in, fade out finale. Suona su una route audio; uscita WASAPI (condivisa o esclusiva) o ASIO. Mixer sempre attivo per un GO immediato. |
| **Video** | Motore libVLC (H.264, HEVC, VP9, AV1, ProRes, HAP, DNxHD; MP4/MOV/MKV/AVI/WMV…), decodifica hardware. Precaricamento: primo fotogramma ~70 ms dopo il GO, riproduzione fluida a 60 fps. Composizione GPU verso le route video, fade da/verso il nero, opacità, layer, adattamento (letterbox, riempi, stira, originale), volume, loop senza stacchi. L'audio esce sulla scheda della sua route audio, con volume indipendente per ogni video. |
| **Immagine** | PNG/JPG/BMP/GIF/TIFF sulle route video (sopra ai video), layer, opacità, fade, durata (0 = finché fermata). Decodifica anticipata. |
| **Cattura** | Sorgente dal vivo (webcam, HDMI/SDI con scheda di acquisizione) via DirectShow: dispositivo, ingresso audio, risoluzione e frequenza, layer, opacità, fade, volume. Si apre in anticipo quando è la prossima; più cue sulla stessa sorgente la condividono; in pausa l'immagine si congela. |
| **MIDI** | Note On/Off, Control Change, Program Change, Pitch Bend, MIDI Show Control, SysEx. |
| **Rete** | OSC su UDP, OSC su TCP (SLIP), testo/byte su UDP o TCP con escape (`\r \n \t \0 \\ \xHH`). |
| **Attesa** | Pausa temporizzata nella sequenza. |
| **Fade** | Porta volume e/o opacità di una cue in esecuzione a un nuovo valore e, se richiesto, la ferma. |
| **Stop** | Ferma una cue o tutte, con fade opzionale. |

### Sequenza

- **GO** lancia la cue al playhead; **pre-wait** ritarda l'avvio di ogni cue.
- **Follow**: *Nessuno*, *Auto follow* (la successiva parte alla fine), *Wait follow* (parte post-wait secondi dopo la fine). Una cue fermata a mano non fa partire la successiva.
- **Link**: quando una cue parte agisce su un'altra cue per numero: *Avvia* (insieme, con il suo pre-wait), *Ferma*, *Fade out*. Dopo un GO il playhead salta le cue subito sotto già avviate.
- **Giri vietati**: un giro di link *Avvia* (1 avvia 2, 2 avvia 1) viene rifiutato in modifica con il messaggio "Link non consentito: loop 1 -> 2 -> 1 infinito"; i giri attraverso un follow sono consentiti; un giro senza nulla che duri viene fermato dall'engine.
- **Armata / disarmata**: una cue disarmata viene saltata ma mantiene il follow.
- **--SHOW START-- / --SHOW END--**: righe fisse in testa e in coda, non eliminabili né spostabili. Il playhead parte da START; *GO dedicato* decide se START ha un GO proprio. Dopo l'ultima cue il playhead si ferma su END.
- I workspace delle versioni precedenti si convertono all'apertura (route, auto-continue → link).

### Trasporto e comandi

- **GO**, **Pausa / Riprendi** (tutto, o ogni cue singolarmente dal pannello *In esecuzione*, con scrubbing della barra in pausa).
- **Fade cue**: fade out della cue selezionata e poi stop (anche per ogni cue in esecuzione).
- **Stop**: fade out di tutto; premuto due volte entro 1,5 s ferma subito.
- **E-STOP**: ferma subito tutto.
- Avvio della cue selezionata senza spostare il playhead; frecce per spostare il playhead da qualunque punto della finestra.
- Blocco del doppio GO (ms impostabili).

### Lista delle cue e pannelli

- Lista con playhead, tipo, nome, destinazione e colonne opzionali (Visualizza → Colonne): route video/audio, volume e opacità (per le cue Fade il valore dato al target), layer, pre/post-wait, durata, fade in/out, loop, follow, link, armata, note, stato.
- Creazione dalla barra o trascinando file; duplica, sposta, elimina (con conferma).
- **Proprietà cue** a destra: si nasconde (Ctrl+I) e si riapre dal menu o con clic destro su una cue; in Safe si nasconde da solo.
- Pannello **In esecuzione / Log** nascondibile.
- Meter di uscita stereo con picco e clip, mini-meter e indicatore CLIP per ogni cue audio.

### Timeline (Visualizza → Timeline, Ctrl+T)

- Lo show previsto GO per GO: blocchi separati da varchi **GO n** (prossimo verde pieno, futuri verde tenue, passati grigi; varco **START** se START ha un GO dedicato).
- Barre per cue con la forma del livello (fade in/out, cue Fade, Stop); cue di controllo come barre rosa uniformi; cue senza fine tratteggiate; primo fotogramma di video e immagini.
- Corsie per route o per tipo, righe separate per le cue sovrapposte.
- Un cursore verde per ogni GO in corso, sul tempo reale di riproduzione (pause e scrubbing inclusi); cursore arancio nel varco del prossimo GO a show finito. Cue in fade verso lo stop in arancione.
- Zoom, Adatta, *Segui il playhead* (durante lo show la vista avanza con il cursore), **Ricalcola** (rilegge durate e anteprime).

### Uscite, route e test (Strumenti → Impostazioni)

- **Generale**: driver audio (WASAPI/ASIO), dispositivo, latenza, modalità esclusiva, volume master, uscita MIDI predefinita, OSC in ingresso, controllo web.
- **Controllo** (salvati nel workspace): tempo di fade dello Stop, tempo del Fade cue, blocco doppio GO.
- **Route audio**: scheda, coppia di canali, gain da −60 a +12 dB, muto, meter.
- **Route video**: canvas e uscite su monitor o finestra di anteprima; porzione del canvas, posizione e scala, keystone/corner pin, warping fino a 8×8, edge blending con gamma e curva, *Affianca uscite in blend*, griglia di test, identificazione delle uscite.
- **Test video**: fluidità, griglia, barre colore, rampa di grigi, scacchiera, campiture piene.
- **Test audio**: sinusoide, rumore rosa e bianco, sweep logaritmico, canali singoli o alternati.
- **Scorciatoie**: ogni comando riassegnabile o senza tasto.

### Controllo remoto

- **OSC** (UDP, porta 53000), indirizzi compatibili QLab: `/go`, `/stop` (E-STOP), `/panic` (Stop), `/pause`, `/resume`, `/playhead/next`, `/playhead/previous`, `/cue/{n}/start`, `/cue/{n}/stop`, `/cue/{n}/select`.
- **Controllo web** (spento all'inizio): pagina per telefoni e tablet sulla rete locale con prossima cue, timeline dal vivo e tasti GO, Pausa/Riprendi, Fade cue, Stop, E-STOP, frecce del playhead.

### Sicurezza in scena

- **Modalità Safe**: blocca modifiche, impostazioni e operazioni rischiose; GO e trasporto restano attivi; uscire dall'app chiede conferma.
- **Show Check** (Ctrl+K): rundown a buio e in muto che controlla file, formati, livelli (clip previsto), uscite e dispositivi, numeri duplicati, target di Fade/Stop/Link, follow su cue senza fine, loop di link.
- Salvataggio atomico, errori nel log senza finestre bloccanti, timer di sicurezza sugli stop, timer di sistema a 1 ms.

### Strumenti di sviluppo

- `tools/stress/Run-StressTest.ps1`: stress test standard e ripetibile; le misure vanno in `PERFORMANCE.md`.
- Diagnostica con la variabile `NEWQ_DEBUG` (log in `%TEMP%\newq-debug.log`).

---

## Modifiche

### 2026-10-11 - Password e indirizzo del controllo web (commit `e7608bf`)
- **Aggiunto**: password della pagina web in *Impostazioni → Generale → Controllo web*; vuota = pagina libera. Con la password la pagina chiede l'accesso e il browser resta autenticato finché la password non cambia o NewQ non si riavvia; dopo 5 password sbagliate in un minuto il dispositivo aspetta 30 secondi.
- **Aggiunto**: l'indirizzo da aprire sul telefono o tablet compare nelle impostazioni (si aggiorna con la porta) e nella barra di stato, con tutti gli indirizzi nel tooltip; un clic lo copia.
- **Corretto**: i client che inviano `Expect: 100-continue` non aspettano più ~350 ms a ogni comando.

### 2026-10-11 - Tolto il dispositivo audio dalle impostazioni generali (commit `e994b89`)
- **Rimosso**: *Impostazioni → Generale → Dispositivo di uscita*. Sceglieva solo la scheda delle route audio lasciate su "Predefinita", un doppione della scheda che ogni route ha già.
- **Cambiato**: nelle route audio la scheda "Predefinita" è ora la scheda predefinita di Windows.

### 2026-10-12 - Installer e aggiornamenti automatici (commit `833c0b4`)
- **Aggiunto**: installer `NewQ-win-Setup.exe` (installazione per l'utente, senza permessi di amministratore, collegamenti nel menu Start e sul desktop), creato con `tools/release/Build-Release.ps1`; guida in `tools/release/README.md`.
- **Aggiunto**: aggiornamenti automatici dalle release GitHub. All'avvio NewQ installato controlla se c'è una versione nuova, mostra versione e note e chiede se installarla; durante la riproduzione non chiede nulla e lo annota nel log. Gli aggiornamenti scaricano solo le differenze quando possibile.
- **Aggiunto**: *Strumenti → Cerca aggiornamenti…* e la versione installata nel menu.

### 2026-10-12 - Avanzamento nei blocchi della timeline e colori delle cue (commit `9fc54b8`)
- **Cambiato**: niente più cursori sulla timeline: la parte già riprodotta di ogni cue in corso diventa verde, sul tempo reale di riproduzione. Vale anche per la pagina web. *Segui il playhead* funziona come prima.
- **Cambiato**: le cue Cattura non sono più verdi (ora giallo oliva): il verde indica l'avanzamento.
- **Aggiunto**: *Impostazioni → Grafica*: colore di ogni tipo di cue (codice esadecimale o tavolozza, ripristino singolo o di tutti), applicato a badge, timeline e pagina web.

### 2026-10-13 - Cursori o riempimento, lampeggio delle cue Cattura (commit `5bd09ed`)
- **Aggiunto**: *Impostazioni → Grafica → Avanzamento*: "Riempimento dei blocchi" (predefinito) o "Cursori" (un cursore per GO, come prima). Vale anche per la pagina web.
- **Corretto**: le cue Cattura non mostravano un avanzamento sensato (una sorgente live non ha una posizione): ora, finché sono attive, il loro blocco lampeggia piano di verde, nell'app e nella pagina web.

### 2026-10-18 - Loop nella timeline (commit `dc504f8`)
- **Corretto**: le cue in loop non avanzavano all'inizio, poi seguivano il GO successivo e si fermavano con lui. Ora, come le Cattura e le immagini fisse, finché sono attive il loro blocco lampeggia piano di verde, nell'app e nella pagina web. Anche *Segui il playhead* non si basa più sul tempo di un loop.

### 2026-10-18 - Nuova disposizione della finestra principale (commit `1fce1b8`)
- **Cambiato**: la timeline è spenta all'inizio (Visualizza → Timeline per riaccenderla).
- **Cambiato**: il pannello *In esecuzione* è a destra della lista, al posto delle proprietà, in stile QLab: una riga per cue con sfondo verde che segue l'avanzamento, durata e tempo mancante in sovraimpressione, pulsanti pausa, fade e stop. Colori per pre-wait, pausa e fade; le cue senza fine hanno la riga tutta verde tenue. In pausa si scorre con un clic o trascinando sulla riga.
- **Cambiato**: le *Proprietà cue* sono in basso, a tutta larghezza, divise in pagine (Base, Trigger e le pagine del tipo di cue). Tra cue dello stesso tipo resta aperta la stessa pagina.
- **Cambiato**: il Log non è più una scheda sotto la lista: si apre in una finestra a parte da *Strumenti → Log…*, solo fuori dalla modalità Safe (attivando Safe si chiude).
