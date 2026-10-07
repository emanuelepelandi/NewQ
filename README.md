# NewQ

Show control per Windows ispirato a QLab: una lista di cue lanciate con **GO** che pilotano audio, video, immagini, MIDI e dispositivi in rete.

## Funzioni

| Cue | Cosa fa |
|---|---|
| **Audio** | WAV, MP3, AIFF, FLAC, M4A/AAC, WMA. Volume in dB, inizio/fine (trim), loop, fade in, fade out finale. Mixer sempre attivo per una latenza di GO minima. Suona su una **route audio**. Uscita **WASAPI** (condivisa o esclusiva) o **ASIO**. |
| **Video** | Motore **libVLC** (lo stesso di VLC): H.264, HEVC, VP9, AV1, ProRes, HAP, DNxHD; contenitori MP4/MOV/MKV/AVI/WMV…, con decodifica hardware. Precaricamento automatico: il primo fotogramma appare circa 70 ms dopo il GO, poi la riproduzione è fluida a 60 fps anche con monitor a frequenze diverse. Composizione su **GPU** (Direct3D 11) verso le **route video**. Fade da e verso il nero, volume, loop senza stacchi; l'audio del video esce sulla scheda della sua route audio. |
| **Immagine** | PNG/JPG/BMP/GIF/TIFF sulle stesse route dei video (sempre sopra ai video), con layer, fade e durata. Decodificate in anticipo in background. |
| **MIDI** | Note On/Off, Control Change, Program Change, Pitch Bend, **MIDI Show Control** (GO/STOP/RESUME… per mixer luci e audio), **SysEx** raw. |
| **Rete** | **OSC su UDP**, **OSC su TCP** (framing SLIP, OSC 1.1), **testo/byte su UDP o TCP** con escape (`\r`, `\n`, `\xHH`), per esempio per proiettori PJLink o media server. |
| **Attesa** | Pausa temporizzata nella sequenza. |
| **Fade** | Porta volume e/o opacità di una cue in esecuzione a un nuovo valore, e se serve la ferma. |
| **Stop** | Ferma una cue specifica, o tutte, con un fade opzionale. |

Ogni cue ha **pre-wait**, **post-wait**, modalità **auto-continue** e **auto-follow**, e lo stato *armata/disarmata*.

**Meter audio**:
- **Uscita stereo**, nell'intestazione: zone verde (fino a −12 dBFS), gialla (fino a −3) e rossa (fino a 0), indicatore di picco e pulsante **0 dB** che diventa rosso (**CLIP**) se l'uscita va in clip. Un clic lo azzera.
- **Per ogni cue audio** in esecuzione, un mini-meter. Se la cue va in clip, compare un'etichetta rossa **CLIP** nella sua riga, che si azzera al GO successivo.
- Il clip viene segnalato sia quando un campione supera 0 dBFS (volume troppo alto) sia quando ci sono più campioni consecutivi a fondo scala (file già distorto all'origine).

**Modalità Safe** (Strumenti → Safe, Ctrl+Maiusc+L): da usare durante lo spettacolo.
- **Bloccato**: creazione, eliminazione, spostamento e modifica delle cue, trascinamento di file, Nuovo/Apri, Impostazioni, "Chiudi uscite video" e "Aggiorna dispositivi".
- **Sempre attivo**: GO, Stop, Panic, Pausa e timeline, spostamento del playhead, Show Check e salvataggio.
- Chiudere NewQ con Safe attivo richiede una conferma.

**Pausa e timeline**: ogni cue in esecuzione si può mettere in pausa da sola. In pausa, la barra di avanzamento di audio e video diventa trascinabile.

**Show Check** (Strumenti → Show Check, Ctrl+K): un rundown virtuale a buio e in muto che non riproduce e non mostra nulla. Controlla:
- **File e percorsi**: file mancanti, percorsi assoluti non portabili, file su unità USB o di rete.
- **Formati**: analisi di audio, video (con libVLC) e immagini; punti di inizio e fine fuori dal file.
- **Livelli**: clip previsto dal picco del file più il volume della cue.
- **Uscite e dispositivi**: schermi non collegati, dispositivi MIDI assenti, messaggi MIDI, MSC, SysEx e OSC non validi.
- **Logica delle cue**: numeri duplicati, Fade e Stop verso cue inesistenti o successive, auto-follow su cue che non finiscono mai.

Un doppio clic su un problema seleziona la cue interessata.

**Controllo remoto OSC** (UDP, porta 53000 di default), con indirizzi compatibili QLab:
`/go`, `/stop`, `/panic`, `/pause`, `/resume`, `/playhead/next`, `/playhead/previous`, `/cue/{n}/start`, `/cue/{n}/stop`, `/cue/{n}/select` (accettato anche il prefisso `/newq`).

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

I test non sopravvivono mai allo spettacolo: **Panic**, l'attivazione di **Safe** e la chiusura della finestra li spengono tutti. Lo **Show Check** segnala route audio su schede non collegate o canali inesistenti, route mute, route video senza uscite o su schermi non collegati, nomi duplicati e cue che puntano a route rimosse.

### Comandi da tastiera

| Tasto | Azione |
|---|---|
| Spazio | GO |
| Esc | PANIC (fade out di tutto; premuto di nuovo entro 1,5 s = stop immediato) |
| S | Stop tutto |
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
│   ├─ Network/           OSC (encoder/decoder, bundle, SLIP), player UDP/TCP, listener remoto
│   └─ Midi/              Costruzione messaggi MIDI/MSC/SysEx, player astratto
├─ src/NewQ.App           (WPF, Windows)
│   ├─ Audio/             NAudio: un bus per route audio, voci con fade sui campioni, test signal, WASAPI/ASIO
│   ├─ Video/             Decodifica libVLC in memoria e compositore GPU (Direct3D 11): un thread di
│   │                     render per schermo, geometria delle uscite, edge blend, test pattern
│   ├─ Views/Setup/       Pagine route audio/video e test, editor di keystone e warp
│   ├─ Midi/              Uscite MIDI WinMM
│   ├─ ViewModels/ Views/ Interfaccia (tema scuro ModernWpf)
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

## Distribuzione

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
7. Installer MSI/MSIX con associazione dei file `.newq`.

### TO DO (completato nel branch `routes-and-test-signals`)

- [x] Pagina nelle impostazioni con "Route video" e "Route audio"; nelle cue si sceglie solo la route.
- [x] Route video con uscite, scaling e riposizionamento, warping, keystone correction, edge blending.
- [x] Route audio con uscite e gain in uscita verso la route.
- [x] Pagina per generare e mandare test pattern alle route video, di diversi tipi, inclusa la fluidità in stile Resolume.
- [x] Pagina per generare e mandare test tone, rumore rosa e sweep in frequenza alle route audio.
