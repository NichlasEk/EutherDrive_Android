# Gauntlet DL: inkrementella texturöverföringar

Resident GPU-ersättning kan nu återanvända textur/NCC-data mellan draws.
Flaggan `EUTHERDRIVE_GAUNTDL_GPU_INCREMENTAL=1` aktiverar detta tillsammans
med `EUTHERDRIVE_GAUNTDL_GPU_REPLACE=1` och `EUTHERDRIVE_GAUNTDL_GPU_RESIDENT=1`.
Native-biblioteket behöver byggas om; C-ABI är fortsatt v4.

Varje segment börjar med full uppladdning. Därefter jämförs 1 KiB-block
mot senaste inskickade snapshoten. Bara ändrade block och draw-metadata
kopieras till host-staging och vidare till GPU. Sammanhängande block slås
ihop till kopieringsregioner. Befintliga Vulkan-barriärer ordnar kopiorna
före shaderns läsning. Fallback/reset börjar åter med en full uppladdning.

Det är snapshot-diffning, **inte** dirty-tracking i emulatorns skrivvägar:
hela texturminnet skannas och kopieras fortfarande på CPU per draw.

## Resultat

Två fönster med 128 verkligt ersatta draws vardera, skip 0 respektive 120,
ger byte-exakt samma fullständiga slutmaskin som CPU-referensen efter
replay 6750→7950. Dekomprimerad storlek 98 901 914 byte, SHA-256:
`32f8e9c49dcc5498c044bb602156ef6e82fe5dec64f27c99749f3a6fe298053a`.

| Fönster | Tidigare uppladdning | Nu | Ändrade textur/NCC-block |
|---|---:|---:|---:|
| skip 0 | 1 099 304 960 byte | 50 487 392 byte | 18 432 byte |
| skip 120 | 1 124 470 784 byte | 100 828 352 byte | 21 504 byte |

Första fönstret kräver cirka 21,8 gånger mindre uppladdning, det senare
cirka 11,2 gånger mindre. Totalsiffrorna inkluderar fulla segmentstarter
med texturer, färg/djup och metadata. Readback och synkar är oförändrade:
131/134 inlämningar, 3/6 fulla pixel-readbacks, 64 byte räknare per draw.
Båda körningarna avslutar med `pendingPixels=0` och noll Vulkan-
synkroniseringsvalideringsfel.

Detta visar lägre överföringsvolym, inte spelbart tempo. Endast 128 stödda
draws per körning ersätts; resten använder CPU. Första replayen tog cirka
12,5 s, vilket inte visar någon säker fartökning mot förra steget. Probe-fps
är API-anrop per sekund, inte spelets bildfrekvens. Linux/RTX 4090 är testat,
inte Android.

## Tester

- Två full-state-orakel: `.build-tmp/gpu-incremental-final.warm.gz` och
  `gpu-incremental-later-final.warm.gz`; loggar med motsvarande namn utan
  `-final.warm.gz`, plus `.log`.
- Resident två-draw-test med syntetisk texturflytt: noll avvikande pixlar.
  Utelämnade kopior ger 5 550 avvikelser, som det negativa testet kräver.
  Detta är en testtransformation, inte en observerad gästtexturflytt.
  Loggar: `gpu-incremental-relocation.log`, `gpu-incremental-negative.log`.
- Samma test med flaggan `=0` kontrollerar den gamla full-upload-vägen:
  `gpu-incremental-disabled.log`.
- Batch-ABI-regression och dess negativa texturtest passerar:
  `gpu-incremental-batch-regression.log`.
- Elva capture-gränstester passerar.
- Normalbygget är återställt: Probe/UI bygger utan fel, elva normal-
  gränstester och PCI-regressionen passerar. Normal replay med alla tre
  GPU-flaggorna och obefintlig biblioteksadress laddar inget GPU-bibliotek
  och ger samma full-state-hash (`gpu-incremental-normal-final.warm.gz`).
  Shadern passerar `spirv-val --target-env vulkan1.1`.

Bygg och testkommandon finns i
[verktygets README](../tools/GauntletGpuProbe/README.md#resident-framebuffer-replacement).
Ingen normalbyggd runtime aktiverar dessa diagnostiska flaggor.

## Nästa avgränsning

Öka det verifierade ersättningsfönstret kontrollerat och mät verkliga swaps
per sekund innan fler hastighetslöften. Den kvarvarande fulla CPU-skanningen/
kopieringen och räknarnas fence per draw är tydliga nästa kostnader att mäta.
Write-path dirty-tracking kräver att alla textur/NCC-skrivvägar och
reset/state-load täcks; snapshot-diffningen bör behållas som kontrollreferens.
