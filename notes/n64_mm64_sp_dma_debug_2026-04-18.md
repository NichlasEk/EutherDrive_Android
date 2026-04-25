# N64 Mega Man 64 SP DMA Debug (2026-04-18)

## Mål

Få `Mega Man 64 (USA)` förbi den nuvarande sena haveripunkten och vidare mot faktisk gameplay.

ROM som använts i nästan alla relevanta körningar:

- `/home/nichlas/roms/N64/Mega Man 64 (USA).z64`

## Kort slutsats

Den aktuella huvudbuggen är nu isolerad till **SP DMA write path** i `Ryu64`.

Det som faktiskt förstör exceptionvektorn är inte:

- PI DMA
- vanlig CPU-store från spelets kod
- generell virtuell-till-fysisk adressöversättning

Det som händer är i stället:

- en **SP write-DMA** startas med fel DRAM-destination
- den destinationen blir `0x00000170`
- transfern skriver över `0x00000180..0x000001BF`
- vilket korruptar exceptionvektorsområdet

## Viktigaste verifierade fynd

### 1. PI-spåret var inte den direkta korruptorn

Tidigare PI-spår var värdefulla, men den direkta skrivaren i den här körningen är inte PI.

Vi såg:

- normala tidiga PI-händelser
- ingen direkt PI DMA som matchade själva korruptionsögonblicket
- exceptionvektorn skrevs korrekt först
- korruptionen kom senare

### 2. CPU-PC vid korruptionen är konsekvent

Det sena korruptionsögonblicket sker med:

- `pc=0x800a16cc`

Först såg det ut som om det kunde vara en vanlig CPU byte-store, men mer detaljerad loggning visade att det inte var en vanlig `SB`-väg.

### 3. Den verkliga korruptorn är `origin=sp-dma-write`

Den avgörande loggen var:

- `N64LOWRAMCPU8 ... origin=sp-dma-write`

Det bevisar att byte-skrivningarna in i låg RAM kommer från den interna SP DMA write-vägen.

### 4. Den dåliga transfern är nu exakt identifierad

Vi fångade en hel dålig SP DMA:

- `startMem=0x0fa0`
- `endMem=0x0ff0`
- `startDram=0x00000170`
- `endDram=0x000001c0`

Alltså träffar den:

- `0x00000180..0x000001BF`

vilket sammanfaller med exceptionvektorsområdet som förstörs.

### 5. SP DMA-pathen fungerar också korrekt i andra fall

I samma sena fas såg vi också fullt rimliga SP write-DMA till:

- `0x00170000`
- `0x00170170`
- `0x004666a8`

Det betyder:

- SP DMA-pathen är inte globalt trasig
- problemet är smalare: **en specifik DMA startar med fel DRAM-adress**

## Nuvarande starkaste hypotes

Felet är nästan säkert ett av dessa två:

1. `SP_DRAM_ADDR` skrivs faktiskt fel, alltså till något i stil med `0x00000170`
2. `SP_DRAM_ADDR` skrivs korrekt först, men vår register-write eller register-read-semantik gör att värdet blir `0x00000170` när `SP_WR_LEN` startar DMA:t

Just nu lutar arbetet mot att detta är en **SP-registersemanikfråga**, inte ett generellt CPU-, PI- eller TLB-problem.

## Exakt vad som redan är instrumenterat

### I `Ryu64/Ryu64.MIPS/Memory.cs`

Det finns nu riktad diagnostik för:

- PI DMA-armning och completion
- låg-RAM CPU stores
- exceptionvektorsskrivningar
- `WriteUInt8`-origin
- SP DMA start/bytevis write/end
- SP register writes för:
  - `SP_MEM_ADDR`
  - `SP_DRAM_ADDR`
  - `SP_WR_LEN`

Relevanta loggtaggar:

- `N64PIDMA`
- `N64LOWRAMCPU`
- `N64LOWRAMCPU8`
- `N64LOWRAMCPU16`
- `N64EXCVEC`
- `N64SPDMA`
- `N64SPREG`

### I `Ryu64/Ryu64.MIPS/Interpreter/InstInterpLoadStore.cs`

Det finns också en smal temporär trace kring `0x800A16C0..0x800A16E0`, men den visade mest att CPU-PC:t råkar stå på ett `SW` medan den verkliga skrivvägen är intern SP DMA.

Den är inte längre huvudspåret.

## Vad som fortfarande saknas

Det som ännu inte fångats klart i ett enda sammanhängande utdrag är raden eller raderna precis före den dåliga DMA:n:

- `SP_MEM_ADDR write`
- `SP_DRAM_ADDR write`
- `SP_WR_LEN write`

som leder fram till:

- `startDram=0x00000170`

Tidigare `tail`-kommandon kapade loggen precis där det blev som mest intressant.

## Högsta signal nästa steg

Målet nu är att avgöra om den dåliga DMA:n startas från:

- ett verkligt lågt `SP_DRAM_ADDR`-registervärde

eller från:

- en intern feltolkning/felmaskning av ett egentligen högre registervärde

### Exakt fråga att besvara

Finns det en loggrad som visar:

- `SP_DRAM_ADDR write value=0x00000170`

eller något mycket nära det?

Om ja:

- då skrivs det dåliga värdet in före DMA-starten, och vi måste följa vem som producerar det

Om nej:

- då är `SP_DRAM_ADDR`-hanteringen i emulatorn fel mellan registerwrite och faktisk DMA-start

## Rekommenderad arbetsordning

### 1. Få fram den saknade SP-registersekvensen

Kör på befintlig logg eller ny körning:

```bash
rg -n "SP_DRAM_ADDR write|SP_MEM_ADDR write|SP_WR_LEN write|startDram=0x00000170" /tmp/mm64_pi.log
```

och gärna:

```bash
rg -n "\\[N64SPREG\\]|startDram=0x00000170|dram=0x00000170|dram=0x0000017|\\[N64SPDMA\\]" /tmp/mm64_pi.log | head -n 220
```

### 2. Om låg `SP_DRAM_ADDR` verkligen skrivs

Fortsätt bakåt från den registerwrite som sätter det värdet:

- vilken CPU-instruktion skriver registret
- vilka register används
- kommer värdet från RAM, stack, RSP-resultat eller någon MMIO-spegel

### 3. Om låg `SP_DRAM_ADDR` inte skrivs men DMA ändå startar lågt

Inspektera exakt denna semantik:

- `SP_DRAM_ADDR_WRITE_EVENT()`
- hur `SP_DRAM_ADDR_REG_RW` lagras
- hur `ExecuteSpDma()` läser tillbaka adressen
- eventuell byte/word-ordning
- eventuell maskning
- eventuell alignment-/roundinglogik

Särskilt misstänkta feltyper:

- fel endian/byteplacering i registerarray
- fel mask som nollar höga adressbitar
- fel partial-write-semantik
- fel användning av ett speglat registervärde i stället för det senast skrivna fulla värdet

## Vad vi inte bör lägga tid på just nu

Undvik att åter öppna bredare spår innan SP-registerkedjan är klarlagd.

Inte högsta signal just nu:

- PI completion-omtag
- generell low-RAM CPU-store-debug
- TLB/virt->phys-teorier
- generell RSP bring-up
- bred VI/SI/MI-timinggissning

Allt det var tidigare rimligt, men det aktuella blockerande fyndet är nu smalare.

## Praktisk definition av "klar nästa del"

Nästa del är klar först när vi kan säga en av följande två meningar med hög säkerhet:

1. "Spelet/emulerad kod skriver faktiskt `SP_DRAM_ADDR=0x00000170` här."
2. "Emulatorn läser eller bygger `SP_DRAM_ADDR` fel; registerwriten var inte låg men DMA-starten blev låg."

Först därefter är det rationellt att patcha själva semantiken.

## Om patch ska göras direkt efter nästa fynd

### Fall A: låg `SP_DRAM_ADDR` skrivs explicit

Gör då inte en bred workaround först.

Spåra i stället:

- den exakta skrivinstruktionen
- dess operands
- källan till registervärdet

Målet är att förstå varför systemet producerar låg adress.

### Fall B: `SP_DRAM_ADDR`-write ser korrekt ut men DMA startar lågt

Då är den sannolika rätta patchen lokal i `Memory.cs`, kring:

- registerwrite-semantik
- registerreadback
- DMA-startkonsumtion av registervärden

## Kort status

Vi är inte längre i "bred bring-up".

Vi är i en smal och högsignal-fas:

- exceptionvektorn korruptas av SP write-DMA
- den felaktiga transfern är identifierad exakt
- kvarvarande uppgift är att fånga och förklara hur just `startDram=0x00000170` uppstår
