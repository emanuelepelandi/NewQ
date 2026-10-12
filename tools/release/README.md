# Rilasciare una nuova versione di NewQ

Lo script `Build-Release.ps1` crea l'installer e i pacchetti di aggiornamento con [Velopack](https://velopack.io) e, se richiesto, li pubblica come **release GitHub** del repository `emanuelepelandi/NewQ`. Le copie di NewQ già installate controllano le release a ogni avvio e chiedono se installare la nuova versione.

## Una volta sola: preparazione

1. **.NET**: serve l'SDK .NET 7 (per compilare NewQ) e un **runtime .NET 8 o successivo** (per lo strumento di pacchettizzazione `vpk`). Su questo PC c'è già il runtime .NET 9. Controllo:
   ```powershell
   dotnet --list-sdks
   dotnet --list-runtimes
   ```
2. **Token GitHub** (serve solo per pubblicare):
   1. GitHub → foto profilo → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** → **Generate new token**.
   2. *Repository access*: **Only select repositories** → `NewQ`.
   3. *Permissions* → *Repository permissions* → **Contents: Read and write**.
   4. Genera e copia il token (inizia con `github_pat_`). Non metterlo in nessun file del repository.

La prima esecuzione dello script scarica `vpk` da nuget.org in `tools\release\.vpk` (esclusa da git).

## Ogni nuova versione

1. **Scegli il numero di versione**: `MAGGIORE.MINORE.CORREZIONE`, sempre più alto del precedente (es. `0.1.0` → `0.2.0` per nuove funzioni, `0.2.1` per correzioni). Un numero già pubblicato non si riusa. Aggiorna anche `<Version>` in `Directory.Build.props`, così le build di sviluppo mostrano lo stesso numero.
2. **Scrivi le note della versione** in un file Markdown, per esempio `notes.md` copiando le voci nuove di `CHANGELOG.md`. Gli utenti le vedono nella richiesta di aggiornamento (le prime righe).
3. **Fai commit e push** delle modifiche.
4. **Prova in locale** (non pubblica nulla):
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0
   ```
   L'installer è `artifacts\releases\NewQ-win-Setup.exe`: provalo su un PC di prova o su questo.
5. **Pubblica**:
   ```powershell
   $env:GITHUB_TOKEN = "github_pat_..."
   powershell -ExecutionPolicy Bypass -File tools\release\Build-Release.ps1 -Version 0.2.0 -Notes notes.md -Publish
   ```
   Lo script ripete i passi della prova, scarica la release precedente per creare l'aggiornamento "delta" (solo le differenze) e crea su GitHub la release `v0.2.0` con installer e pacchetti.
6. **Controlla** su `https://github.com/emanuelepelandi/NewQ/releases` che la release `v0.2.0` contenga `NewQ-win-Setup.exe`, `NewQ-win-Portable.zip`, `NewQ-0.2.0-full.nupkg` (e il `-delta.nupkg` dalla seconda versione in poi) e `releases.win.json`.

Opzioni dello script: `-SkipTests` salta i test (solo per prove veloci), `-Token` passa il token invece della variabile `GITHUB_TOKEN`.

## Cosa succede sui PC con NewQ installato

- **Installazione**: si esegue `NewQ-win-Setup.exe` (dalla pagina della release). NewQ si installa per l'utente in `%LocalAppData%\NewQ`, senza permessi di amministratore, con collegamenti nel menu Start e sul desktop. Impostazioni (`%AppData%\NewQ`) e workspace non vengono toccati da installazioni e aggiornamenti.
- **Aggiornamenti**: qualche secondo dopo l'avvio NewQ controlla le release. Se ce n'è una più nuova mostra versione e note e chiede *Installare ora?*. Con **Sì** chiede di salvare il workspace se serve, scarica, si chiude e riparte aggiornato. Con **No** non succede nulla fino al prossimo avvio. Se all'avvio ci sono già cue in riproduzione non chiede nulla: annota la versione nel log.
- **Manuale**: *Strumenti → Cerca aggiornamenti…*; sotto c'è la versione installata.
- Le build di sviluppo e la cartella `dist` (da `publish.ps1`) non si aggiornano: solo la versione installata con il setup.

## Da sapere

- **Avviso di Windows**: l'installer non è firmato digitalmente, quindi al primo avvio Windows SmartScreen può mostrare *"Windows ha protetto il PC"*: *Ulteriori informazioni* → *Esegui comunque*. Per toglierlo serve un certificato di firma del codice (a pagamento); Velopack lo supporta con le opzioni `--signParams` di `vpk pack`.
- **Tornare indietro**: gli aggiornamenti vanno solo verso versioni più alte. Per annullare una versione difettosa si pubblica una nuova versione (es. `0.2.1`) con il codice corretto.
- **Repository pubblico**: le app leggono le release senza credenziali perché il repository è pubblico. Se diventasse privato gli aggiornamenti smetterebbero di funzionare.
- La versione di `vpk` nello script (`$VpkVersion`) deve restare uguale a quella del pacchetto `Velopack` in `src\NewQ.App\NewQ.App.csproj`.
