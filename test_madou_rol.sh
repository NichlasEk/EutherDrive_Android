#!/bin/bash
cd /home/nichlas/EutherDrive
echo "Kör Madou test..."
./bin/headless/EutherDrive.Headless madou 2>&1 | grep -A1 "013A50\|ROL-MADOU-CRITICAL" | head -20