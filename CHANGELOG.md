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

