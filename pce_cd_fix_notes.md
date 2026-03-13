# PCE-CD Fix Notes

Den här filen sammanfattar den PCE/CD-runda som gjorde bland annat `Golden Axe` spelbart igen och förbättrade flera andra PC Engine CD-spel markant.

## Symptom vi jagade

- `Golden Axe` hade korrupt grafik i introt och under gameplay.
- `Golden Axe` kunde också låsa under CD-audio/video-sekvenser.
- `Steam-Heart's` låste i gameplay från savestate.
- `Strider` krävde Arcade Card.
- `Valis II` och andra spel hade intermittenta grafiska fel eller konstig audio-state.

Det viktiga i efterhand är att det inte var ett enda fel. Det var flera modellfel i samma system:

- CPU/bus/video kördes för grovt per frame eller scanline
- CDDA/SCSI-kommandon hade fel completion/state-semantik
- SubQ och CD-audio-position följde inte riktig media-position tillräckligt bra
- ADPCM/CDDA-modellerna var för förenklade

## Viktigaste fixområden

### 1. CPU/bus/video gick över till finare tidsmodell

Filer:

- [PceCdAdapter.cs](/home/nichlas/EutherDrive/EutherDrive.Core/PceCdAdapter.cs)
- [HuC6280.cs](/home/nichlas/EutherDrive/PCE_CD_Core/HuC6280.cs)
- [BUS.cs](/home/nichlas/EutherDrive/PCE_CD_Core/BUS.cs)

Vad som ändrades:

- PCE-kärnan kör nu CPU:n instruktion för instruktion via `Step()`.
- Bus, timer, video och CD-audio clockas med de exakta cykler som instruktionen tog.
- I/O-waitstates matas in som riktiga extra CPU-cykler.
- `RunFrame()` fick högre säkerhetsgräns så frame-rendering inte avbröts mitt i bilden.

Varför det mattered:

- Många av de “korrupta frames” vi såg var egentligen följder av för grov scheduling.
- När CPU, VDC och CD-ROM inte interleavas rätt blir både grafik och CD-beteende fel även om varje del för sig ser rimlig ut.

### 2. CDDA-progression och track-end-semantik rättades

Filer:

- [CDRom.cs](/home/nichlas/EutherDrive/PCE_CD_Core/CDRom.cs)

Vad som ändrades:

- CDDA-progress frikopplades från ren mix-konsumtion och clockas via bus/cycles.
- `AudioEndPos` mode `2` och mode `3` fick bättre state/completion-beteende.
- Inclusive track-end/sector-end-semantik rättades.
- `AudioStartPos` sätter nu default stop-läge till spårslut i stället för att lämna ett ogiltigt/för gammalt stoppvärde.

Varför det mattered:

- `Golden Axe` fastnade tidigare i audio-polling när CD-positionen inte avancerade rätt.
- `Valis II` kunde starta musik men bete sig konstigt om `AudioStartPos` lämnade fel stop-state.

Relevant commit:

- `1f39082` `pce-cd: default cdda stop to track end on audio start`

### 3. SCSI/SubQ blev mycket närmare riktig CD-data

Filer:

- [CDRom.cs](/home/nichlas/EutherDrive/PCE_CD_Core/CDRom.cs)

Vad som ändrades:

- `ReadSubCodeQ` använder rå `.sub`-data när den finns.
- SubQ följer media-/audio-sektor bättre i stället för att bygga allt syntetiskt.
- Flera SCSI command completion-paths rättades så status/message/busy beter sig närmare referens.

Varför det mattered:

- `Golden Axe` låste tidigare i introsekvenser kring `SubChannelQ`.
- Efter att riktig Q-kanal och bättre CDDA-state användes försvann en hel klass av “pollar för evigt”-fel.

### 4. ADPCM gick från grov approximation till Geargrafx-nära modell

Filer:

- [ADPCM.cs](/home/nichlas/EutherDrive/PCE_CD_Core/ADPCM.cs)
- [CDRom.cs](/home/nichlas/EutherDrive/PCE_CD_Core/CDRom.cs)

Vad som ändrades:

- ADPCM är nu cykelklockad i stället för att dekodas direkt i `GetSample()`.
- `play pending`, `length`, reset och half/end-IRQ följer Geargrafx mycket närmare.
- DMA/read/write för ADPCM har egen intern state.
- `CDRom.ClockAudio()` clockar nu även ADPCM varje gång bussen clockas.

Varför det mattered:

- Den gamla modellen band ADPCM-dekodningen till hostens audio pull, vilket är fel nivå av modellering.
- Den nya modellen stabiliserade inte bara ljudvägen utan gav också stora bieffekter på grafik/timing, eftersom hela PCE-CD-systemet slutade “dra snett” tidsmässigt.

Observera:

- `Valis II`-rösten är fortfarande inte helt löst. Musik fungerar, men tal verkar fortfarande ha ett separat ADPCM-problem kvar.
- Trots det gav ADPCM-porten en tydlig vinst på grafikstabilitet i flera spel.

### 5. PPU/VDC-debug och timingarbete hjälpte att isolera roten

Filer:

- [PPU.cs](/home/nichlas/EutherDrive/PCE_CD_Core/PPU.cs)
- [BUS.cs](/home/nichlas/EutherDrive/PCE_CD_Core/BUS.cs)
- [Program.cs](/home/nichlas/EutherDrive/EutherDrive.Headless/Program.cs)

Vad som lades till:

- pixel/sprite/SAT/VRAM/VCE-trace
- VDC bus-trace
- bättre headless-körning från PCE-savestates och raw states
- snapshot/dump-stöd för att jämföra korrupta och icke-korrupta scener

Varför det mattered:

- Vi kunde visa att flera “spriteproblem” egentligen inte var spriteproblem.
- Vi kunde skilja på:
  - fel i renderingen
  - fel i producerad VRAM/palett
  - fel i CD/SCSI/CDDA-state
  - fel i scheduler/interleaving

## Resultat

Det viktigaste resultatet av den här rundan är:

- `Golden Axe` fungerar nu korrekt
- flera andra PCE-CD-spel blev plötsligt spelbara
- flera gamla låsningar i CD-audio/SCSI försvann
- grafisk stabilitet blev mycket bättre överlag

Det här verkar alltså ha varit ett systemiskt timingproblem i PCE-CD-kedjan, inte bara en enskild sprite- eller tilebugg.

## Kvar att göra

- `Valis II`: tal/röst saknas fortfarande trots att musik fungerar
- fortsatt ADPCM-jämförelse mot Geargrafx read/write-slot-latency kan behövas
- om fler grafiska avvikelser återstår är nästa starka spår fortfarande VDC line-event/latch-timing, inte generell tiledecode

## Samlingscommit

Den stora samlingscommitten för den här rundan är:

- `0e13e96` `pce: improve timing, adpcm, and video stability`

Det är den committen som i praktiken flyttade PCE-CD-läget från “många konstiga delproblem” till “flera spel fungerar”.
