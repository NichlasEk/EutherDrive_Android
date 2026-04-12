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

### ⚙️ steel_fortress.apa
**Steel Fortress** - Industriellt metallskin med:
- Kall borstad stålpalett
- Polerad metallglans över UI:t
- Nitdetaljer i overlay-lagret
- Skarpare, maskinella paneler

### 🫧 floating_glass.apa
**Floating Glass** - Transparent svävskin med:
- Nästan osynliga glaspaneler
- Ljus text och tunna flytande konturer
- Kall cyan/guld-accent för ett lätt holografiskt uttryck
- UI som känns frikopplat från bakgrunden

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

## Metallglanslager

Du kan slå på ett generellt metallglans-overlay från vilken `.apa`-fil som helst via `[custom]`.

```toml
[custom]
metal_sheen = "true"
metal_sheen_opacity = "0.16"
metal_sheen_angle = "-24"
metal_sheen_band_thickness = "0.14"
metal_sheen_edge_opacity = "0.12"
metal_sheen_tint = "#DCE6F2"
metal_sheen_edge_tint = "#F6FAFF"
metal_rivets = "true"
metal_rivet_count = "7"
metal_rivet_radius = "4.6"
metal_rivet_inset = "16"
metal_rivet_opacity = "0.82"
metal_rivet_tint = "#C0CAD4"
```

- `metal_sheen`: slår av/på overlay-lagret
- `metal_sheen_opacity`: total styrka, `0.0` till `1.0`
- `metal_sheen_angle`: vinkel på glansbanden i grader
- `metal_sheen_band_thickness`: bredd på glansbanden, `0.02` till `0.45`
- `metal_sheen_edge_opacity`: hur stark top-/kantglansen är
- `metal_sheen_tint`: huvudton för metallreflexen
- `metal_sheen_edge_tint`: ljusare ton för skarpa highlights
- `metal_rivets`: slår av/på nitdetaljer längs kanterna
- `metal_rivet_count`: antal nitar längs överkanten
- `metal_rivet_radius`: storlek på nitarna
- `metal_rivet_inset`: avstånd från kanterna
- `metal_rivet_opacity`: styrka/kontrast i nitarna
- `metal_rivet_tint`: baston för nitmetallen

## Metallytor

Om du vill att paneler, knappar och inputfält ska få mer metallisk materialkänsla, inte bara ett overlay, kan du även slå på metallytor:

```toml
[custom]
metal_surface = "true"
metal_surface_gloss = "0.86"
metal_surface_contrast = "0.26"
metal_surface_specular = "#F8FBFF"
metal_surface_shadow = "#05080C"
```

- `metal_surface`: slår på metalliska gradientmaterial för paneler, knappar och inputs
- `metal_surface_gloss`: hur stark den polerade highlighten är
- `metal_surface_contrast`: hur hårt materialet går mellan ljus och skugga
- `metal_surface_specular`: färgton för de ljusaste reflexerna
- `metal_surface_shadow`: färgton för de mörkaste metallskuggorna

## Liquid Chrome

För stora paneler och `screen-shell` finns även en separat procedural render-väg för mer flytande kromreflektioner:

```toml
[custom]
liquid_chrome = "true"
liquid_chrome_intensity = "1.04"
liquid_chrome_warp = "1.35"
liquid_chrome_bands = "7"
liquid_chrome_coolness = "0.28"
liquid_chrome_specular = "#F8FBFF"
liquid_chrome_shadow = "#03070B"
```

- `liquid_chrome`: aktiverar den dedikerade chrome-renderern för större ramar/paneler
- `liquid_chrome_intensity`: hur starka de speglande banden är
- `liquid_chrome_warp`: hur mycket reflektionerna böjs och flyter
- `liquid_chrome_bands`: antal stora reflektionsband
- `liquid_chrome_coolness`: hur kall/blå kromtonen känns
- `liquid_chrome_specular`: högdagerfärg
- `liquid_chrome_shadow`: mörk bas/skuggton

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
