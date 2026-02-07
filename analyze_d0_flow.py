#!/usr/bin/env python3
"""
Analysera hur D0 får värdet 0x00070000 före 0x013A50
"""

print("Analys av D0 flöde i Madou Monogatari")
print("=" * 50)

print("\nProblem:")
print("- Vid PC=0x013A50 har D0 = 0x00070000")
print("- Efter ROL.L #8 blir D0 = 0x07000000")
print("- Efter ANDI.W #$00FF blir D0 fortfarande 0x07000000 (FEL)")
print("- Spelet behöver D0 = 0x00000007 efter ANDI.W")

print("\nLösning behövs:")
print("- D0 ska vara 0x07000000 vid PC=0x013A50")
print("- Då ger ROL.L #8 -> 0x00000007")
print("- ANDI.W #$00FF -> 0x00000007")
print("- ASL.W #5 -> 0x000000E0 (korrekt palette index)")

print("\nMöjliga orsaker till 0x00070000 istället för 0x07000000:")
print("1. Byte order fel när värde läses från minne")
print("2. Fel sign/zero extension av 16-bit till 32-bit")
print("3. Fel i shift/rotate instruktion före 0x013A50")
print("4. Fel i MOVE/MOVEA/LEA instruktion")
print("5. Initieringsfel vid spelstart")

print("\nVad kan producera 0x00070000?")
print("- MOVE.L #$00070000, D0")
print("- LSL.L #8 på 0x00000700")
print("- ROR.L #8 på 0x07000000")
print("- ROL.L #24 på 0x07000000")
print("- Felaktig läsning av 32-bit värde")

print("\nVad kan producera 0x07000000?")
print("- MOVE.L #$07000000, D0")
print("- ROL.L #8 på 0x00070000")
print("- LSL.L #8 på 0x00070000")
print("- Felaktig läsning där bytes är omvända")

print("\nByte representation:")
print("0x00070000 = bytes [00, 07, 00, 00]")
print("0x07000000 = bytes [07, 00, 00, 00]")
print("0x00000700 = bytes [00, 00, 07, 00]")
print("0x00000007 = bytes [00, 00, 00, 07]")

print("\nAnalys av transformationer:")
print("0x00070000 ROL.L #8 -> 0x07000000")
print("0x07000000 ROL.L #8 -> 0x00000007")
print("0x00000007 ROL.L #8 -> 0x00000700")
print("0x00000700 ROL.L #8 -> 0x00070000")

print("\nDet är en 4-stegs cykel!")
print("Om spelet kör ROL.L #8 varje frame:")
print("Frame 1: 0x00070000 -> 0x07000000")
print("Frame 2: 0x07000000 -> 0x00000007")
print("Frame 3: 0x00000007 -> 0x00000700")
print("Frame 4: 0x00000700 -> 0x00070000 (tillbaka)")

print("\nSå om D0 börjar med 0x00070000:")
print("- Frame 1: ANDI.W ger fel (0x07000000)")
print("- Frame 2: ANDI.W ger rätt (0x00000007)")
print("- Frame 3: ANDI.W ger fel (0x00000700)")
print("- Frame 4: ANDI.W ger fel (0x00070000)")

print("\nEndast var 4:e frame skulle fungera!")
print("Men spelet behöver fungera varje frame.")

print("\nDärför MÅSTE D0 vara 0x07000000 vid start av varje frame!")
print("Något sätter D0 till 0x00070000 felaktigt.")

print("\nNästa steg:")
print("1. Hitta instruktion som sätter D0 till 0x00070000")
print("2. Förstå varför den sätter fel värde")
print("3. Fixa antingen instruktionen eller initieringen")
print("4. Testa om spelet fungerar med D0 = 0x07000000")

print("\nDebug strategi:")
print("- Logga ALLA skrivningar till D0")
print("- Fånga exakt när 0x00070000 sätts")
print("- Disassemblera koden vid den PC:n")
print("- Förstå algoritmen som beräknar D0")
