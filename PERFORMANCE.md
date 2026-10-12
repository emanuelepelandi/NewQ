# NewQ - misure di prestazioni

Dal 2026-10-11 le prove si fanno con `tools/stress/Run-StressTest.ps1`, sempre con lo stesso scenario (*go-overlap*), e lo script aggiunge qui una sezione per ogni esecuzione. Le misure precedenti sono trascritte sotto: vengono da script provvisori, con scenari simili ma non identici, quindi vanno confrontate con cautela.

Macchina di prova: Intel Core i7-8700 3.20 GHz | NVIDIA GeForce GTX 1070 | 16 GB | Windows 11 Home 22000.
Video di prova: `BearGrylls-contro i castori infernali(PARODIA).mp4` (5,7 MB, H.264, ~1:32) e `COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4` (176 MB, 4K 60 fps, ~5:13), in `D:\Download`.

## Misure precedenti (trascritte)

### 2026-10-09 - Audio dei video (uscita DirectSound) - commit `e6c4f34`

Workspace con i due video, uscita in finestra di anteprima; livello udibile = picco della sessione audio × volume della sessione, finestre di 250 ms.

| Misura | Prima (build `dist`, mmdevice) | Dopo |
|---|---|---|
| Livello udibile dopo GO del video 1 con il video 2 in precaricamento (6 s) | 0,00 in tutte le finestre | 0,07 - 0,23 |
| Video 1 a 0 dB + video 2 a -60 dB sopra | - | 0,20 (video 1 ancora udibile) |

Stress: 48 GO video in 12 giri da 4 (700 ms tra i GO), Panic doppio a fine giro.

| Misura | Valore |
|---|---|
| GO senza audio | 0 / 48 |
| Livello dopo ogni GO | min 0,18 - medio 0,23 |
| Working set | 240 -> 238 MB |
| Player libVLC rimasti | 0 |

### 2026-10-10 - Un cursore per GO, Follow e Link - commit `752aea4`

Workspace di 48 video, GO sovrapposti (6 per giro, 350 ms tra i GO), pausa/ripresa al primo giro, Panic doppio a fine giro.

| Misura | 24 GO (4 giri) | 72 GO (12 giri) |
|---|---|---|
| Comando GO (UIA), ms | medio 7 - max 104 | medio 3 - max 8 |
| Working set inizio -> fine, MB | 252 -> 318 | 245 -> 234 |
| Working set a inizio giro, MB | - | 269 357 357 362 355 353 361 365 322 313 236 237 |
| Player libVLC rimasti | - | 0 |

### 2026-10-11 - Pannelli, stati dei GO, fade arancione - commit `e0a0943`

Stesso scenario da 72 GO (12 giri da 6, 350 ms), con i fade dei Panic che colorano la timeline.

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | medio 4 - max 45 |
| Working set inizio -> fine, MB | 289 -> 243 |
| Working set a inizio giro, MB | 426 455 453 422 462 417 455 420 324 310 243 242 |
| Rendering timeline (campioni nel log), ms | medio 9,5 - max 29,5 (4 campioni) |
| Player libVLC / cue rimasti | 0 / 0 |

### Sessioni precedenti (riassunto)

| Prova | Risultato |
|---|---|
| Fade e cue Live, 100 cicli | +31 MB di working set |
| Timeline con 300 cue | rendering ~2,5 ms, ricostruzione ~19 ms; ritardo UI p99 150-210 ms solo all'avvio dei video |
| Pool di player libVLC di riserva (poi rimosso) | memoria fino a 14 GB: soluzione scartata |
| Creazione di un player libVLC | attesa di 20-380 ms su un lock di libVLC |

## Esecuzioni standard

## 2026-10-11 14:16 - go-overlap - prima esecuzione standard

- Commit: `e0a0943+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 2.9 - p95 4.5 - max 21.2 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.25 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 31.1 - p99 medio 18.3 - max assoluto 127.5 |
| Rendering timeline, ms | media 6.3 - max 16.0 (6 campioni) |
| Ricostruzione timeline, ms | media 9.3 - max 16.5 (2) |
| CPU per giro, % (tutti i core) | media 16.0 - max 20.8 |
| Working set, MB | inizio 263 - picco 364 - fine 253 |
| Private bytes, MB | inizio 300 - fine 238 |
| Memoria gestita, MB (fine) | 17 |
| GC gen0 / gen2 (totale) | 156 / 99 |
| Handle | inizio 1231 - fine 1151 |
| Thread | inizio 70 - fine 53 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 332 335 338 356 348 349 352 364 341 350 350 344 |

## 2026-10-11 15:16 - go-overlap - Stop / E-STOP, pagina Controllo

- Commit: `b46438b+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.7 - p95 9.5 - max 14.7 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 34.9 - p99 medio 18.2 - max assoluto 127.4 |
| Rendering timeline, ms | media 6.1 - max 28.0 (6 campioni) |
| Ricostruzione timeline, ms | media 9.1 - max 15.8 (2) |
| CPU per giro, % (tutti i core) | media 15.2 - max 16.2 |
| Working set, MB | inizio 250 - picco 363 - fine 250 |
| Private bytes, MB | inizio 264 - fine 241 |
| Memoria gestita, MB (fine) | 18 |
| GC gen0 / gen2 (totale) | 158 / 101 |
| Handle | inizio 1223 - fine 1159 |
| Thread | inizio 70 - fine 54 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 352 342 343 356 363 354 355 353 355 344 354 337 |

## 2026-10-11 15:30 - go-overlap - barre di controllo, timeline segue il cursore

- Commit: `16bb33f+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.5 - p95 9.8 - max 19.5 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 34.4 - p99 medio 21.7 - max assoluto 109.9 |
| Rendering timeline, ms | media 5.2 - max 27.5 (9 campioni) |
| Ricostruzione timeline, ms | media 9.2 - max 16.1 (2) |
| CPU per giro, % (tutti i core) | media 15.5 - max 16.4 |
| Working set, MB | inizio 268 - picco 360 - fine 249 |
| Private bytes, MB | inizio 298 - fine 235 |
| Memoria gestita, MB (fine) | 18 |
| GC gen0 / gen2 (totale) | 161 / 103 |
| Handle | inizio 1233 - fine 1156 |
| Thread | inizio 70 - fine 54 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 355 351 350 357 354 355 360 345 346 353 359 310 |

## 2026-10-11 16:21 - go-overlap - controllo web (spento), layout timeline condiviso

- Commit: `aeabb13+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.7 - p95 6.8 - max 10.7 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 30.7 - p99 medio 18.3 - max assoluto 109.0 |
| Rendering timeline, ms | media 3.2 - max 16.6 (9 campioni) |
| Ricostruzione timeline, ms | media 9.1 - max 16.2 (2) |
| CPU per giro, % (tutti i core) | media 15.1 - max 16.3 |
| Working set, MB | inizio 259 - picco 455 - fine 249 |
| Private bytes, MB | inizio 263 - fine 244 |
| Memoria gestita, MB (fine) | 23 |
| GC gen0 / gen2 (totale) | 159 / 100 |
| Handle | inizio 1239 - fine 1163 |
| Thread | inizio 71 - fine 55 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 341 362 355 349 346 358 354 348 368 352 455 343 |

### 2026-10-11 - Controllo web (prova specifica, non standard)

Istanza di prova con il controllo web attivo (porta 8090), workspace di 2 video + 1 Fade, 10 pagine collegate al flusso di stato (`/api/events`), 400 comandi `up`/`down` via `POST /api/cmd/...` a 20 ms di distanza.

| Misura | Valore |
|---|---|
| Latenza comando HTTP, ms | media 1,0 - p95 1,3 - max 15,9 |
| Comandi falliti | 0 / 400 |
| Ritardo thread UI durante la prova, ms | p99 39-45, max 46 (playhead spostato 400 volte) |
| Working set | 229 -> 244 MB |
| Handle / thread | 1110 -> 1134 / 49 -> 52 |
| GO dalla pagina (playhead su 1, GO, E-STOP) | 10 / 10 andati a buon fine |


## 2026-10-11 21:35 - go-overlap - password del controllo web

- Commit: `6526c04+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.2 - p95 5.3 - max 13.7 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 50.6 - p99 medio 19.5 - max assoluto 133.9 |
| Rendering timeline, ms | media 4.2 - max 28.6 (9 campioni) |
| Ricostruzione timeline, ms | media 14.9 - max 27.6 (2) |
| CPU per giro, % (tutti i core) | media 16.7 - max 25.6 |
| Working set, MB | inizio 248 - picco 356 - fine 250 |
| Private bytes, MB | inizio 249 - fine 249 |
| Memoria gestita, MB (fine) | 18 |
| GC gen0 / gen2 (totale) | 161 / 103 |
| Handle | inizio 1233 - fine 1148 |
| Thread | inizio 70 - fine 53 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 333 343 342 354 345 344 355 341 351 356 353 340 |

## 2026-10-11 21:44 - go-overlap - rimosso dispositivo audio generale

- Commit: `f33fd1e+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.7 - p95 11.9 - max 17.1 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 39.3 - p99 medio 20.7 - max assoluto 148.3 |
| Rendering timeline, ms | media 1.7 - max 2.7 (8 campioni) |
| Ricostruzione timeline, ms | media 11.9 - max 21.3 (2) |
| CPU per giro, % (tutti i core) | media 15.9 - max 18.2 |
| Working set, MB | inizio 253 - picco 434 - fine 246 |
| Private bytes, MB | inizio 267 - fine 235 |
| Memoria gestita, MB (fine) | 21 |
| GC gen0 / gen2 (totale) | 162 / 104 |
| Handle | inizio 1232 - fine 1149 |
| Thread | inizio 70 - fine 53 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 339 348 353 344 348 350 340 344 342 434 350 323 |

## 2026-10-12 23:28 - go-overlap - Program.Main con Velopack

- Commit: `96d1710+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.9 - p95 12.9 - max 19.0 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 31.7 - p99 medio 18.1 - max assoluto 124.1 |
| Rendering timeline, ms | media 4.8 - max 30.4 (9 campioni) |
| Ricostruzione timeline, ms | media 9.3 - max 15.8 (2) |
| CPU per giro, % (tutti i core) | media 15.4 - max 19.4 |
| Working set, MB | inizio 248 - picco 357 - fine 272 |
| Private bytes, MB | inizio 258 - fine 249 |
| Memoria gestita, MB (fine) | 16 |
| GC gen0 / gen2 (totale) | 159 / 101 |
| Handle | inizio 1229 - fine 1165 |
| Thread | inizio 71 - fine 55 |
| Oggetti GDI / USER | inizio 37 / 67 - fine 37 / 66 |
| Working set a fine giro, MB | 354 344 352 353 356 346 353 355 354 354 357 331 |

## 2026-10-12 23:48 - go-overlap - progresso nei blocchi + colori cue

- Commit: `fb9d365+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.0 - p95 5.7 - max 6.9 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 34.5 - p99 medio 20.1 - max assoluto 109.5 |
| Rendering timeline, ms | media 1.5 - max 2.2 (8 campioni) |
| Ricostruzione timeline, ms | media 9.8 - max 17.4 (2) |
| CPU per giro, % (tutti i core) | media 16.0 - max 24.4 |
| Working set, MB | inizio 247 - picco 367 - fine 253 |
| Private bytes, MB | inizio 257 - fine 237 |
| Memoria gestita, MB (fine) | 22 |
| GC gen0 / gen2 (totale) | 149 / 91 |
| Handle | inizio 1243 - fine 1163 |
| Thread | inizio 72 - fine 55 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 347 365 359 356 360 358 360 367 357 361 357 339 |

## 2026-10-13 00:05 - go-overlap - scelta cursori/avanzamento + lampeggio Cattura

- Commit: `e8cce48+modifiche` (build: `Debug`) - Macchina: Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz | NVIDIA GeForce GTX 1070 | 16 GB | Microsoft Windows 11 Home 22000
- Parametri: 12 giri x 6 GO, 700 ms tra i GO, 72 video (BearGrylls-contro i castori infernali(PARODIA).mp4, COSTA RICA IN 4K 60fps HDR (ULTRA HD).mp4), anteprima in finestra
- Esito: processo vivo True, player libVLC rimasti 0, cue in esecuzione rimaste 0

| Misura | Valore |
|---|---|
| Comando GO (UIA), ms | media 3.3 - p95 6.2 - max 8.4 (72 GO) |
| Audio dopo ogni GO (picco) | min 0.19 - media 0.26 - GO muti 0 / 72 |
| Ritardo thread UI, ms (finestre di 5 s) | p99 peggiore 32.8 - p99 medio 20.6 - max assoluto 133.0 |
| Rendering timeline, ms | media 4.6 - max 28.6 (9 campioni) |
| Ricostruzione timeline, ms | media 14.0 - max 25.6 (2) |
| CPU per giro, % (tutti i core) | media 16.3 - max 24.6 |
| Working set, MB | inizio 272 - picco 362 - fine 256 |
| Private bytes, MB | inizio 292 - fine 247 |
| Memoria gestita, MB (fine) | 19 |
| GC gen0 / gen2 (totale) | 145 / 87 |
| Handle | inizio 1243 - fine 1159 |
| Thread | inizio 71 - fine 54 |
| Oggetti GDI / USER | inizio 37 / 65 - fine 37 / 66 |
| Working set a fine giro, MB | 336 347 361 352 356 351 349 351 362 353 354 328 |
