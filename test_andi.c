#include <stdio.h>
#include <stdint.h>

int main() {
    // Test ANDI.W behavior
    uint32_t d0 = 0x07000000;
    
    printf("Initial D0: 0x%08X\n", d0);
    printf("Low 16 bits: 0x%04X\n", d0 & 0xFFFF);
    
    // ANDI.W #$00FF, D0
    uint16_t low_word = d0 & 0xFFFF;  // Read low 16 bits
    uint16_t result_word = low_word & 0x00FF;  // AND with immediate
    d0 = (d0 & 0xFFFF0000) | result_word;  // Write back preserving high 16 bits
    
    printf("After ANDI.W #$00FF, D0: 0x%08X\n", d0);
    printf("Expected by Madou: 0x00000700\n");
    
    // What if ANDI.W cleared high bits?
    d0 = 0x07000000;
    d0 = result_word;  // Clear high bits
    printf("If ANDI.W cleared high bits: 0x%08X\n", d0);
    
    // What if we're reading wrong 16 bits?
    d0 = 0x07000000;
    uint16_t high_word = (d0 >> 16) & 0xFFFF;
    result_word = high_word & 0x00FF;
    d0 = result_word;  // Word operation, high bits cleared?
    printf("If reading high word: high_word=0x%04X, result=0x%04X, D0=0x%08X\n", 
           high_word, result_word, d0);
    
    // What about little-endian storage?
    union {
        uint32_t l;
        uint8_t b[4];
        uint16_t w[2];
    } reg;
    
    reg.l = 0x07000000;
    printf("\nLittle-endian storage:\n");
    printf("Bytes: [0x%02X, 0x%02X, 0x%02X, 0x%02X]\n", 
           reg.b[0], reg.b[1], reg.b[2], reg.b[3]);
    printf("w[0] (low word): 0x%04X\n", reg.w[0]);
    printf("w[1] (high word): 0x%04X\n", reg.w[1]);
    
    return 0;
}