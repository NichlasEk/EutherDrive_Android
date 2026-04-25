# Prompt: N64 Mega Man 64 SP DMA Corruption Handoff

Du arbetar i `/home/nichlas/EutherDrive_Android` och ska fortsätta en pågående högsignal-debug av `Mega Man 64 (USA)` i `Ryu64`.

## Målet

Fortsätt från det nuvarande läget och isolera varför en felaktig **SP DMA write** korruptar exceptionvektorn i låg RAM.

## Det som redan är bevisat

- den sena korruptionen sker runt `pc=0x800a16cc`
- det är **inte PI DMA**
- det är **inte en vanlig CPU byte-store**
- det är **inte generell virt->phys-översättning**
- den verkliga skrivvägen är:
  - `origin=sp-dma-write`

Den dåliga transfern är fångad:

- `startMem=0x0fa0`
- `endMem=0x0ff0`
- `startDram=0x00000170`
- `endDram=0x000001c0`

Det betyder att SP write-DMA skriver över:

- `0x00000180..0x000001BF`

och därmed förstör exceptionvektorsområdet.

## Det som också är bevisat

SP DMA-pathen fungerar inte bara fel.

I samma sena fas finns också normala SP write-DMA till adresser som:

- `0x00170000`
- `0x00170170`
- `0x004666a8`

Så problemet är smalt:

- en **specifik** SP DMA startas med fel DRAM-destination

## Viktigaste frågan nu

Ta reda på vilket av dessa två som är sant:

1. `SP_DRAM_ADDR` skrivs faktiskt som ett lågt värde, exempelvis `0x00000170`
2. `SP_DRAM_ADDR` skrivs rimligt, men vår registersemanik gör att DMA:n ändå startar med `0x00000170`

## Relevanta filer

Fokusera främst på:

- `Ryu64/Ryu64.MIPS/Memory.cs`

Sekundärt:

- `Ryu64/Ryu64.MIPS/Interpreter/InstInterpLoadStore.cs`

## Relevanta loggtaggar

- `N64SPREG`
- `N64SPDMA`
- `N64LOWRAMCPU8`
- `N64EXCVEC`

## Först: få fram den saknade kedjan i loggen

Nuvarande loggar visar redan den dåliga DMA:n, men tidigare utdrag kapade precis raderna som sätter upp den.

Använd till exempel:

```bash
rg -n "SP_DRAM_ADDR write|SP_MEM_ADDR write|SP_WR_LEN write|startDram=0x00000170" /tmp/mm64_pi.log
```

och:

```bash
rg -n "\\[N64SPREG\\]|startDram=0x00000170|dram=0x00000170|dram=0x0000017|\\[N64SPDMA\\]" /tmp/mm64_pi.log | head -n 220
```

## Om du ser att `SP_DRAM_ADDR` verkligen skrivs lågt

Då ska du:

- följa den exakta skrivinstruktionen
- identifiera registervärdena som används
- avgöra om värdet kommer från RAM, stack, MMIO-spegel eller något tidigare RSP/CPU-resultat

Undvik att lägga in workaround först om du inte måste.

## Om `SP_DRAM_ADDR`-write ser rimlig ut men DMA startar lågt

Då ska du inspektera och eventuellt patcha:

- `SP_DRAM_ADDR_WRITE_EVENT()`
- hur `SP_DRAM_ADDR_REG_RW` lagras
- hur `ExecuteSpDma()` läser tillbaka adressen
- maskning, alignment och endian/byteplacering

Särskilt misstänkt:

- partial-write-semantik
- fel mask som tappar höga bitar
- fel användning av registerspegel

## Arbetssätt

- håll dig till det här spåret tills du vet exakt hur `0x00000170` uppstår
- gå inte tillbaka till breda PI/TLB/VI/SI-hypoteser om inte ny data tvingar det
- prioritera små, riktade loggar och verifierbara slutsatser
- om du patchar registersemantik, verifiera direkt att den dåliga transfern försvinner

## Definition av framsteg

Arbetet är i rätt riktning först när du kan säga en av dessa:

- "`SP_DRAM_ADDR` skrivs faktiskt lågt före den dåliga DMA:n."
- "`SP_DRAM_ADDR` skrivs inte lågt; emulatorn förvränger värdet före DMA-start."

Det är den närmaste riktiga beslutsgränsen.
