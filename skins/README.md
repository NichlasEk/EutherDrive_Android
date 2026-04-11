# EutherDrive .apa Skins

Detta är skins-mappen för EutherDrive emulatorn. Skins använder `.apa` formatet (Avalonia Presentation Assets) som är TOML-baserade konfigurationsfiler.

## Tillgängliga Skins

### 🇸🇪 svensk_viking.apa
**Svensk Viking** - Ett stolt svenskt tema med:
- Gul och blå från Sveriges flagga (#006AA7 och #FECC00)
- Vikingainspirerad estetik
- Run-liknande typsnitt (Cinzel)
- Blå knappar med gula hover-effekter
- Perfekt för svenska användare!

### 🎨 default.apa
**EutherDrive Default** - Den klassiska mörka temat med:
- Mörkblå/svart bakgrund
- Turkosa accenter (#5EEAD4)
- Modern, minimalistisk design

### 🔥 neon_pink.apa
**Neon Pink Cyberpunk** - Cyberpunk-tema med:
- Rosa och lila neonlys
- Hög kontrast
- Futuristisk känsla

### 📺 retro_amber.apa
**Retro Amber CRT** - Klassisk terminalstil med:
- Bärnstensfärgad monokrom
- CRT-skärmseffekt
- Retro typsnitt (VT323)

## Så här skapar du ett eget skin

1. Kopiera `default.apa` som mall
2. Ändra färger, typsnitt och värden
3. Spara med ett nytt namn (t.ex. `mitt_skin.apa`)
4. Klicka på "🎨 Skin"-knappen i EutherDrive
5. Välj "Load from File..." och välj din .apa-fil!

## Färgformat

Använd hex-färger:
```toml
accent = "#FECC00"        # Vanlig hex
panel_glass = "#CC0D2847" # Med alpha (CC = 80% opacitet)
```

## Sektioner

- `[skin]` - Metadata (namn, författare, version)
- `[colors]` - Alla färger (bakgrund, paneler, text, accenter)
- `[gradient.hero]` - Huvudgradienten
- `[typography]` - Typsnitt och storlekar
- `[layout]` - Avstånd, padding, rundade hörn
- `[buttons]` - Knappstilar
- `[panels]` - Panelutseende
- `[inputs]` - Textfält och dropdowns
- `[effects]` - Transparens, glow, animationer
- `[custom]` - Egna inställningar för speciella skins

## Tips

- Använd [Adobe Color](https://color.adobe.com) för att skapa färgscheman
- Testa ditt skin direkt i emulatorn - ändringar appliceras direkt!
- Dela dina skins med communityn!

## Exempel: Svenska flaggans färger

```toml
# Sveriges flagga
svensk_blå = "#006AA7"
svensk_gul = "#FECC00"

# Mörkare varianter för bakgrunder
blå_mörk = "#004B7C"
blå_djup = "#051424"
```

---

**Lycka till med skin-skapandet!** 🎨🇸🇪
