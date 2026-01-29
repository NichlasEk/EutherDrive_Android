# Headless savestates — metod

## Översikt
Headless laddar savestates på två sätt:
1) **Auto‑load slot 1 på boot** (samma som UI‑slots)
2) **Explicit fil‑load** via `--load-savestate <rom> <savestate_file> [frames]`

## 1) Auto‑load slot 1 (UI‑kompatibelt)
Miljövariabler:
- `EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1`
- `EUTHERDRIVE_SAVESTATE_DIR=/path/to/savestates` (valfri, default `./savestates`)

Exempel:
```
EUTHERDRIVE_LOAD_SLOT1_ON_BOOT=1 \
EUTHERDRIVE_SAVESTATE_DIR=/home/nichlas/EutherDrive/savestates \
dotnet run --project EutherDrive.Headless -- /home/nichlas/roms/Aladdin.md 300
```

Vad som sker:
- Laddar ROM
- Använder `SavestateService` med `EUTHERDRIVE_SAVESTATE_DIR` (eller `./savestates`)
- Läser slot 1 och kör 60 warm‑up frames

## 2) Explicit fil‑load
Kommandot:
```
dotnet run --project EutherDrive.Headless -- --load-savestate <rom_path> <savestate_path> [frames]
```

Exempel:
```
dotnet run --project EutherDrive.Headless -- --load-savestate \
  /home/nichlas/roms/Aladdin.md \
  /home/nichlas/EutherDrive/savestates/Aladdin__Europe_3fbaee79.euthstate \
  300
```

Vad som sker:
- Laddar ROM
- Läser **den exakta filen** du anger
- Väljer slot via `EUTHERDRIVE_SAVESTATE_SLOT` (default 1) om filen innehåller fler slots

## Slot‑override (valfri)
För filer med flera slots:
```
EUTHERDRIVE_SAVESTATE_SLOT=2
```

## Vanliga fel
- **"array rank unreasonable"**: oftast savestate skapat av äldre/annan serializer‑version.
  - Lösning: skapa om savestate i nuvarande UI‑build.

## Snabb debug‑checklista
- Verifiera att ROM‑hash matchar savestate‑filens hash.
- Testa auto‑load slot 1 först (som UI gör).
- Testa explicit fil‑load med `--load-savestate`.
