# NewQ 0.3.0

- **Scheda grafica**: *Impostazioni → Generale → Video* sceglie la GPU di composizione e uscite (predefinita di Windows o una scheda precisa).
- **Decodifica video su GPU**: i video erano decodificati sempre dalla CPU; ora usano la GPU (D3D11VA/DXVA2), con *Decodifica hardware* per tornare alla CPU. CPU in riproduzione da 13 a 11 % (massimo da 23 a 12 %).
- **Test pattern**: *Fluidità* con barre a 60/50/30/25/24 fps e contatore dei frame; *Griglia* simmetrica dal centro con cerchi negli angoli, frecce sui bordi, area di sicurezza e righe di 1 pixel; *Barre colore* SMPTE con PLUGE; *Rampa di grigi* con gradini numerati e zone vicino a nero e bianco; testi in Segoe UI.
- **Stop e PANIC**: **Stop** (S) ferma subito la cue selezionata; **PANIC** (Esc) sfuma tutte le cue in esecuzione nel tempo impostato (due volte = immediato).
- **Effetti, scala e posizione** per video e immagini: Hue, saturazione, contrasto, luminosità, inverti colore; scala X/Y e posizione in pixel. Sulla GPU, modificabili mentre la cue è in onda.
- **Cue Nota**: riga separatrice nella lista; follow e link funzionano.
- **Route facoltative**: si possono eliminare tutte se nessuna cue le usa; creando una cue senza la sua route NewQ apre la pagina delle route. Un nuovo workspace non apre più la finestra di anteprima.
- **.NET 10**: NewQ passa a .NET 10 LTS.
