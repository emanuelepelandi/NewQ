# NewQ

Show control per Windows ispirato a QLab: una lista di cue lanciate con **GO** che pilotano audio, video, immagini, MIDI e dispositivi in rete.

## Funzioni

| Cue | Cosa fa |
|---|---|
| **Audio** | WAV, MP3, AIFF, FLAC, M4A/AAC, WMA. Volume in dB, inizio/fine (trim), loop, fade in, fade out finale. Mixer sempre attivo per una latenza di GO minima. Suona su una **route audio**. Uscita **WASAPI** (condivisa o esclusiva) o **ASIO**. |
| **Video** | Motore **libVLC** (lo stesso di VLC): H.264, HEVC, VP9, AV1, ProRes, HAP, DNxHD; contenitori MP4/MOV/MKV/AVI/WMV…, con decodifica hardware. Precaricamento automatico: il primo fotogramma appare circa 70 ms dopo il GO, poi la riproduzione è fluida a 60 fps anche con monitor a frequenze diverse. Composizione su **GPU** (Direct3D 11) verso le **route video**. Fade da e verso il nero, volume, loop senza stacchi; l'audio del video esce sulla scheda della sua route audio, con un volume indipendente per ogni video (uscita DirectSound di libVLC: con l'uscita WASAPI predefinita tutti i video condividono un unico volume, e il precaricamento di un video ammutoliva quello in riproduzione). |
| **Immagine** | PNG/JPG/BMP/GIF/TIFF sulle stesse route dei video (sempre sopra ai video), con layer, fade e durata. Decodificate in anticipo in background. |
| **Cattura** | Una sorgente dal vivo (videocamera, sorgente HDMI/SDI tramite scheda di acquisizione) sulle route video, come un video senza fine: layer, opacità, fade, audio della sorgente sulla sua route. La sorgente si apre in anticipo quando la cue diventa la prossima; più cue sulla stessa sorgente la condividono. In pausa l'immagine si congela. Usa il modulo DirectShow di libVLC, vedi sotto. |
| **MIDI** | Note On/Off, Control Change, Program Change, Pitch Bend, **MIDI Show Control** (GO/STOP/RESUME… per mixer luci e audio), **SysEx** raw. |
| **Rete** | **OSC su UDP**, **OSC su TCP** (framing SLIP, OSC 1.1), **testo/byte su UDP o TCP** con escape (`\r`, `\n`, `\xHH`), per esempio per proiettori PJLink o media server. |
| **Attesa** | Pausa temporizzata nella sequenza. |
| **Fade** | Porta volume e/o opacità di una cue in esecuzione a un nuovo valore, e se serve la ferma. |
| **Stop** | Ferma una cue specifica, o tutte, con un fade opzionale. |

Ogni cue ha **pre-wait** (attesa prima di partire), lo stato *armata/disarmata* e:
- **Follow**: *Nessuno*; *Auto follow*, la cue successiva parte quando questa finisce; *Wait follow*, parte **post-wait** secondi dopo la fine. Una cue fermata a mano non fa partire la successiva.
- **Link**: quando la cue parte (GO, follow, link, OSC), agisce su un'altra cue indicata per numero: *Avvia* (con il suo pre-wait: per far partire più cue insieme), *Ferma*, *Fade out* (durata "Fade cue" delle impostazioni). Dopo un GO il playhead salta le cue subito sotto che il GO ha già fatto partire. Due o più cue non possono avviarsi a vicenda con dei link (1 avvia 2, 2 avvia 1): ripartirebbero all'infinito nello stesso istante, quindi la modifica che chiude il giro viene annullata subito, con un messaggio; un file che ne contiene uno lo segnala all'apertura e nello Show Check, e ne parte solo il primo giro. Un giro che passa per un follow è invece consentito (aspetta la fine delle cue: lo show si ripete); se però non contiene nulla che duri e gira a vuoto, l'engine lo ferma.

I workspace salvati con le versioni precedenti si convertono all'apertura: ogni *auto-continue* diventa un link *Avvia* verso la cue successiva, che riceve il vecchio post-wait come pre-wait. Lo show suona come prima.

**--SHOW START-- e --SHOW END--**: ogni workspace inizia e finisce con due righe fisse, che non fanno nulla e non si possono eliminare, spostare o rinominare; le nuove cue vanno sempre tra le due. Aprendo un workspace il playhead è su **--SHOW START--**. Nelle sue proprietà, **GO dedicato**: se attivo, START ha un GO tutto suo (il primo GO non fa nulla, in timeline è il varco *START*, prima di GO 1); se no (predefinito per i nuovi workspace), il primo GO fa già partire la prima cue. In ogni caso START non conta nella numerazione dei GO. Dopo l'ultima cue il playhead si ferma su **--SHOW END--**, anche quando tutto ha finito di suonare, così si può continuare a spostarlo con le frecce. Le note di entrambe si possono modificare.

**Meter audio**:
- **Uscita stereo**, nell'intestazione: zone verde (fino a −12 dBFS), gialla (fino a −3) e rossa (fino a 0), indicatore di picco e pulsante **0 dB** che diventa rosso (**CLIP**) se l'uscita va in clip. Un clic lo azzera.
- **Per ogni cue audio** in esecuzione, un mini-meter. Se la cue va in clip, compare un'etichetta rossa **CLIP** nella sua riga, che si azzera al GO successivo.
- Il clip viene segnalato sia quando un campione supera 0 dBFS (volume troppo alto) sia quando ci sono più campioni consecutivi a fondo scala (file già distorto all'origine).

**Modalità Safe** (Strumenti → Safe, Ctrl+Maiusc+L): da usare durante lo spettacolo.
- **Bloccato**: creazione, eliminazione, spostamento e modifica delle cue, trascinamento di file, Nuovo/Apri, Impostazioni, Log (la finestra si chiude), "Chiudi uscite video" e "Aggiorna dispositivi".
- **Sempre attivo**: GO, Stop, E-STOP, Pausa e timeline, spostamento del playhead, Show Check e salvataggio.
- Chiudere NewQ con Safe attivo richiede una conferma.

**Pausa e timeline**: ogni cue in esecuzione si può mettere in pausa da sola. In pausa, la barra di avanzamento di audio e video diventa trascinabile.

**Show Check** (Strumenti → Show Check, Ctrl+K): un rundown virtuale a buio e in muto che non riproduce e non mostra nulla. Controlla:
- **File e percorsi**: file mancanti, percorsi assoluti non portabili, file su unità USB o di rete.
- **Formati**: analisi di audio, video (con libVLC) e immagini; punti di inizio e fine fuori dal file.
- **Livelli**: clip previsto dal picco del file più il volume della cue.
- **Uscite e dispositivi**: schermi non collegati, dispositivi MIDI assenti, messaggi MIDI, MSC, SysEx e OSC non validi.
- **Logica delle cue**: numeri duplicati, Fade e Stop verso cue inesistenti o successive, follow su cue che non finiscono mai, link verso cue inesistenti.

Un doppio clic su un problema seleziona la cue interessata.

**Controllo remoto OSC** (UDP, porta 53000 di default), con indirizzi compatibili QLab:
`/go`, `/stop` (E-STOP), `/panic` (Stop), `/pause`, `/resume`, `/playhead/next`, `/playhead/previous`, `/cue/{n}/start`, `/cue/{n}/stop`, `/cue/{n}/select` (accettato anche il prefisso `/newq`).

### Controllo web

*Impostazioni → Generale → Controllo web* (spento all'inizio): NewQ serve una pagina per telefoni e tablet sulla rete locale, all'indirizzo mostrato nella barra di stato (es. `http://192.168.1.10:8080`). La pagina mostra la prossima cue e le cue *In esecuzione* come nell'app (riga verde che segue l'avanzamento, durata e tempo mancante, stessi colori per pre-wait, pausa e fade, pulsanti pausa, fade e stop per ogni cue), e ha i tasti GO, Pausa/Riprendi, Fade cue, Stop, E-STOP e le frecce del playhead (anche da tastiera: frecce, Spazio, Esc). I comandi arrivano come dai tasti dell'app e sono registrati nel log; funzionano anche in modalità Safe. Con una **password** (stesse impostazioni; vuota = pagina libera) la pagina chiede prima l'accesso; il browser resta autenticato finché la password non cambia o NewQ non si riavvia; dopo 5 password sbagliate quel dispositivo aspetta 30 secondi. La pagina usa HTTP semplice: la password protegge da accessi casuali, non da chi intercetta la rete, quindi usarla su una rete dello show, non su una pubblica. L'indirizzo da aprire è nelle impostazioni e nella barra di stato (il tooltip li elenca tutti, un clic lo copia). La prima volta Windows può chiedere il permesso del firewall per le reti private.

Tecnica: server HTTP integrato (`src/NewQ.App/Remote`), senza dipendenze; stato in tempo reale con Server-Sent Events (`/api/events`, 10 Hz quando cambia), comandi con `POST /api/cmd/{go|pause|fade|stop|estop|up|down}` e, per una cue in esecuzione, `POST /api/cue/{id}/{pause|fade|stop}`; accesso con `POST /login` (cookie di sessione).

### Route, test e configurazione delle uscite

Le cue non scelgono più un monitor o una scheda audio: scelgono una **route**. Le route si configurano in **Strumenti → Impostazioni** (o direttamente dalle voci *Route audio…*, *Route video…*, *Test pattern video…*, *Test tone e rumore rosa…*). Le modifiche si applicano subito e si salvano con il workspace; i file della versione precedente vengono convertiti all'apertura.

**Route audio**: nome, scheda audio (WASAPI; con ASIO vale il driver scelto in *Generale*), coppia di canali di uscita in base ai canali reali della scheda, **gain di uscita** da −60 a +12 dB e muto, con meter dal vivo. Gain e muto non interrompono ciò che suona; cambiare scheda o canali ferma le cue in corso.

**Route video**: ogni route ha un **canvas** (risoluzione) e una o più **uscite**, ciascuna su un monitor/proiettore o nella finestra di anteprima. Per ogni uscita:
- **Porzione del canvas** mostrata e **posizione/scala** sullo schermo.
- **Keystone / corner pin**: si trascinano i 4 angoli nell'editor (Ctrl = trascinamento fine, frecce = 0,1 %, Maiusc+frecce = 1 %, Canc = azzera).
- **Warping**: griglia fino a 8×8 di punti interpolati in modo morbido (Catmull-Rom).
- **Edge blending** sui quattro lati, con gamma del proiettore e forma della curva. *Affianca uscite in blend* imposta in un clic porzioni, sovrapposizione e blend per più proiettori affiancati.
- *Griglia di test su questa route* e *Identifica uscite* (numero e cornice colorata su ogni uscita).

**Test video**: si inviano alle route selezionate pattern generati dalla GPU, ognuno per un controllo preciso: **fluidità** in stile Resolume (barre e quadrato in moto costante, striscia di 60 frame, contatore: scatti, judder, frame persi), **griglia** di allineamento (keystone, warp, fuoco, overscan), **barre colore**, **rampa di grigi** (gamma, nero, banding), **scacchiera**, campiture piene **bianco / grigio 50 % / nero / rosso / verde / blu** (uniformità, pixel difettosi, zone di blend).

**Test audio**: **tono sinusoidale** (20 Hz–20 kHz, preset rapidi), **rumore rosa**, **rumore bianco** e **sweep logaritmico** (da/a/durata), livello in dBFS (−20 di default, avviso sopra −10), su entrambi i canali, uno solo o **alternati** ogni secondo per verificare il cablaggio. Le modifiche valgono subito sui test in corso; ogni route mostra il proprio meter.

I test non sopravvivono mai allo spettacolo: **Stop**, l'attivazione di **Safe** e la chiusura della finestra li spengono tutti. Lo **Show Check** segnala route audio su schede non collegate o canali inesistenti, route mute, route video senza uscite o su schermi non collegati, nomi duplicati e cue che puntano a route rimosse.

**Fade cue** (trasporto, tasto F): sfuma suono e immagine della cue selezionata e poi la ferma, nel tempo impostato in *Impostazioni → Controllo*, dove si imposta anche il fade dello **Stop** (fade out di tutto). **E-STOP** ferma subito tutto, senza fade. Ogni cue nel pannello *In esecuzione* ha anche il proprio pulsante di fade. Una cue che sta sfumando verso lo stop diventa arancione nella timeline.

**Timeline** (Visualizza → Timeline, Ctrl+T; spenta all'inizio): sotto la lista, a tutta larghezza, lo show previsto GO per GO. Ogni GO apre un blocco con le cue che lancia (pre-wait, follow, link); ogni blocco è preceduto da un varco tratteggiato con l'etichetta **GO n** (verde pieno: il prossimo GO; verde tenue: i successivi; grigio: quelli già passati, sempre rispetto al playhead): lì l'operatore preme GO, e il tempo di attesa non è contato. Ogni cue è una barra la cui forma è il suo livello nel tempo: fade in, fade out, cue Fade che la abbassano, Stop che la chiudono. Le cue di controllo (Fade, Attesa) sono barre rosa uniformi, senza livello. Le cue senza fine (loop, live, immagini fisse) proseguono tratteggiate nei blocchi successivi finché qualcosa non le ferma. Video e immagini mostrano il primo fotogramma; la durata dei video viene letta dal file. Corsie per route o per tipo di cue; zoom (Ctrl+rotella), "Adatta", "Segui il playhead" (a show fermo mostra il prossimo GO; durante lo show la vista avanza con lo show quando esce dallo schermo); durante lo show la parte già riprodotta di ogni cue in corso diventa verde, sul tempo reale di riproduzione (pause e scorrimenti inclusi); in alternativa (*Impostazioni → Grafica → Avanzamento: Cursori*) un cursore verde per ogni GO in corso e uno arancio nel varco del prossimo GO a show finito. Le cue senza fine attive (loop, Cattura, immagini fisse), che non hanno una posizione nello show, lampeggiano piano di verde. Il verde è riservato all'avanzamento: i colori dei tipi di cue (badge e timeline) si cambiano in *Impostazioni → Grafica*. La pagina web segue le stesse scelte. Un clic su una cue la seleziona. Le anteprime si generano in background e mai mentre lo show è in riproduzione; una lettura incompleta viene ritentata da sola, e **Ricalcola** rilegge tutti i file e ricostruisce la timeline.

**In esecuzione** (a destra della lista, in stile QLab): una riga per ogni cue in corso, con sfondo verde che si riempie con l'avanzamento, durata e tempo mancante in sovraimpressione e i pulsanti pausa, fade e stop. Il colore dice lo stato: verde in corso, giallo in pre-wait, blu in pausa, arancio in fade verso lo stop; le cue senza fine (loop, Cattura, immagini fisse) hanno la riga tutta di un verde tenue e "∞". In pausa si scorre la cue con un clic o trascinando sulla riga.

**Proprietà cue** (in basso, a tutta larghezza): le proprietà della cue selezionata divise in pagine: *Base* (numero, nome, note, armata), *Trigger* (pre/post-wait, follow, link) e le pagine del tipo (per esempio *Video*, *Layer*, *Tempo e loop*). Passando a una cue dello stesso tipo resta aperta la stessa pagina.

**Log** (Strumenti → Log…): i messaggi dell'app in una finestra a parte, disponibile solo fuori dalla modalità Safe.

**Visualizza**: il pannello *Proprietà cue* si nasconde (freccia nell'intestazione, Ctrl+I) per dare spazio alla lista e si riapre dal menu o con un clic destro su una cue; in modalità Safe si nasconde da solo. Anche il pannello *In esecuzione* si può nascondere. *Colonne della lista* sceglie quali proprietà mostrare: destinazione, route video e audio, volume, opacità (per le cue Fade: il valore finale dato al target), layer, pre/post-wait, durata, fade in/out, loop, follow, link, armata, note, stato. Le cue senza quella proprietà lasciano la cella vuota. Scelte e dimensioni dei pannelli sono salvate per macchina.

**Scorciatoie da tastiera** (Strumenti → Scorciatoie da tastiera): ogni comando si può riassegnare o lasciare senza tasto; un tasto appartiene a una sola funzione. Mentre si scrive in un campo funzionano solo le combinazioni con Ctrl/Alt e lo Stop. Le scorciatoie sono salvate per macchina e i menu mostrano sempre quelle attuali.

### Comandi da tastiera (predefiniti)

| Tasto | Azione |
|---|---|
| Spazio | GO |
| Esc | Stop (fade out di tutto; premuto di nuovo entro 1,5 s = immediato) |
| F | Fade out della cue selezionata |
| S | E-STOP (ferma subito tutto) |
| P | Pausa / Riprendi |
| Ctrl+Invio | Avvia la cue selezionata senza spostare il playhead |
| Ctrl+↑ / Ctrl+↓ | Sposta la cue |
| Ctrl+D / Canc | Duplica / elimina |
| Ctrl+N / O / S | Nuovo / Apri / Salva |

Puoi trascinare file audio, video e immagini sulla lista: le cue vengono create automaticamente.

## Architettura

```
NewQ.sln
├─ src/NewQ.Core          (.NET, nessuna dipendenza da Windows: testabile)
│   ├─ Model/             Cue e workspace (JSON *.newq, percorsi relativi al file)
│   ├─ Engine/            CueEngine: playhead, GO, pre/post-wait, continue, fade, stop, panic
│   ├─ Signals/           Generatori di toni, rumore rosa/bianco e sweep
│   ├─ Video/             Geometria delle uscite: omografia (keystone), warp, mesh
│   ├─ Timeline/          Previsione dello show GO per GO (blocchi, inviluppi dei fade) per la timeline
│   ├─ Network/           OSC (encoder/decoder, bundle, SLIP), player UDP/TCP, listener remoto
│   └─ Midi/              Costruzione messaggi MIDI/MSC/SysEx, player astratto
├─ src/NewQ.App           (WPF, Windows)
│   ├─ Audio/             NAudio: un bus per route audio, voci con fade sui campioni, test signal, WASAPI/ASIO
│   ├─ Video/             Decodifica libVLC in memoria e compositore GPU (Direct3D 11): un thread di
│   │                     render per schermo, geometria delle uscite, edge blend, test pattern
│   ├─ Views/Setup/       Pagine route audio/video e test, editor di keystone e warp
│   ├─ Midi/              Uscite MIDI WinMM
│   ├─ Themes/            Design system: token (colori, spaziature, raggi, icone) e stili condivisi
│   ├─ ViewModels/ Views/ Interfaccia (ModernWpfUI, docs/NEWQ-DESIGN-SYSTEM.md)
│   └─ Settings/          Impostazioni della macchina (%AppData%\NewQ\settings.json)
└─ tests/NewQ.Core.Tests  xUnit: sequenza, temporizzazioni, protocolli, salvataggio
```

Scelte principali:
- **Il motore è single-thread e separato dai player.** `CueEngine` parla solo con `ICuePlayer` e `IActiveCue`, quindi aggiungere un tipo di cue significa scrivere un player. Nei test il tempo è simulato (`ManualScheduler`), così le temporizzazioni si verificano in modo deterministico.
- **Pensato per l'affidabilità in scena.** Il salvataggio è atomico (file temporaneo e poi swap). Le eccezioni finiscono nel log e non aprono finestre modali che bloccherebbero il GO. C'è un blocco contro il doppio GO, un timer di sicurezza sugli stop e le uscite a schermo intero non si chiudono con Alt+F4. La risoluzione dei timer di sistema è portata a 1 ms.
- **Due livelli di configurazione.** Il workspace (show) è portabile tra computer, mentre dispositivi e porte sono salvati per macchina.

## Sviluppo

Requisiti: .NET SDK 7 o successivo, Windows 10/11.

```powershell
dotnet build
dotnet test tests\NewQ.Core.Tests
dotnet run --project src\NewQ.App -- samples\Demo.newq
```

## Sorgenti live: modulo DirectShow

Le cue Cattura aprono la sorgente con l'ingresso DirectShow di libVLC (`dshow://`), che vede webcam, schede di acquisizione UVC (Elgato, Magewell...) e schede con driver DirectShow (Blackmagic DeckLink, AJA). Il pacchetto NuGet `VideoLAN.LibVLC.Windows` non include questo modulo: è preso dalla build ufficiale di VLC 3.0.21 (firmato VideoLAN, checksum verificato) ed è in `src/NewQ.App/ThirdParty/libvlc`, copiato automaticamente nei plugin di libVLC a ogni build. Vedi il README di quella cartella per provenienza e licenza (LGPL).

Nota: quando una cue Cattura è la prossima del GO la sorgente resta aperta (precaricata), quindi una webcam mostra la sua spia accesa anche prima del GO.

## Distribuzione

**Installer e aggiornamenti automatici** (Velopack, release GitHub):

```powershell
powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0                          # prova in locale
powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0 -Notes notes.md -Publish  # pubblica
```

Crea `artifacts\releases\NewQ-win-Setup.exe` (installazione per l'utente, senza permessi di amministratore) e i pacchetti di aggiornamento; con `-Publish` li carica come release `v0.2.0` del repository. Le copie installate controllano le release all'avvio e chiedono se aggiornarsi (*Strumenti → Cerca aggiornamenti…* per farlo a mano). Guida passo passo: [tools/release/README.md](tools/release/README.md).

**Cartella portabile** (senza installer né aggiornamenti):

```powershell
.\publish.ps1
```

Crea `dist\NewQ\NewQ.exe` e `dist\NewQ-win-x64.zip`: una build *self-contained* che non richiede .NET installato sul PC dello show. Basta copiare la cartella ed eseguire.

## Roadmap suggerita

1. **Passaggio a .NET 8 LTS**: .NET 7 non è più supportato. Basta installare l'SDK 8 e cambiare `net7.0` in `net8.0` nei `.csproj`.
2. Preload dei file audio per un GO ancora più rapido; audio dei video anche su ASIO.
3. **Group cue** (playlist, start simultaneo) e cue list multiple.
4. **Patch di rete**: destinazioni con nome (es. "Banco luci") condivise da più cue.
5. Trigger da **MIDI in** e **hotkey** per singola cue.
6. Video: crossfade diretto tra due video (oggi il fade passa dal nero), maschere per uscita, salvataggio e richiamo di preset di geometria.
7. Associazione dei file `.newq` all'installazione; installer firmato digitalmente.