using System;

namespace EutherDrive.Core.Arcade.Cps2;

// CPS2 decryption translated from MAME's BSD-3-Clause cps2crypt.cpp
// by Paul Leaman, Andreas Naive, Nicola Salmoria and Charles MacDonald.
internal static class Cps2Decrypter
{
    private static readonly int[] Fn1GroupA = { 10, 4, 6, 7, 2, 13, 15, 14 };
    private static readonly int[] Fn1GroupB = { 0, 1, 3, 5, 8, 9, 11, 12 };
    private static readonly int[] Fn2GroupA = { 6, 0, 2, 13, 1, 4, 14, 7 };
    private static readonly int[] Fn2GroupB = { 3, 5, 9, 10, 8, 15, 12, 11 };

    private static readonly SBox[] Fn1R1Boxes =
    {
        new(new byte[]
        {
            0, 2, 2, 0, 1, 0, 1, 1, 3, 2, 0, 3, 0, 3, 1, 2,
            1, 1, 1, 2, 1, 3, 2, 2, 2, 3, 3, 2, 1, 1, 1, 2,
            2, 2, 0, 0, 3, 1, 3, 1, 1, 1, 3, 0, 0, 1, 0, 0,
            1, 2, 2, 1, 2, 3, 2, 2, 2, 3, 1, 3, 2, 0, 1, 3,
        },
        new int[] { 3, 4, 5, 6, -1, -1 },
        new int[] { 3, 6 }),
        new(new byte[]
        {
            3, 0, 2, 2, 2, 1, 1, 1, 1, 2, 1, 0, 0, 0, 2, 3,
            2, 3, 1, 3, 0, 0, 0, 2, 1, 2, 2, 3, 0, 3, 3, 3,
            0, 1, 3, 2, 3, 3, 3, 1, 1, 1, 1, 2, 0, 1, 2, 1,
            3, 2, 3, 1, 1, 3, 2, 2, 2, 3, 1, 3, 2, 3, 0, 0,
        },
        new int[] { 0, 1, 2, 4, 7, -1 },
        new int[] { 2, 7 }),
        new(new byte[]
        {
            3, 0, 3, 1, 1, 0, 2, 2, 3, 1, 2, 0, 3, 3, 2, 3,
            0, 1, 0, 1, 2, 3, 0, 2, 0, 2, 0, 1, 0, 0, 1, 0,
            2, 3, 1, 2, 1, 0, 2, 0, 2, 1, 0, 1, 0, 2, 1, 0,
            3, 1, 2, 3, 1, 3, 1, 1, 1, 2, 0, 2, 2, 0, 0, 0,
        },
        new int[] { 0, 1, 2, 3, 6, 7 },
        new int[] { 0, 1 }),
        new(new byte[]
        {
            3, 2, 0, 3, 0, 2, 2, 1, 1, 2, 3, 2, 1, 3, 2, 1,
            2, 2, 1, 3, 3, 2, 1, 0, 1, 0, 1, 3, 0, 0, 0, 2,
            2, 1, 0, 1, 0, 1, 0, 1, 3, 1, 1, 2, 2, 3, 2, 0,
            3, 3, 2, 0, 2, 1, 3, 3, 0, 0, 3, 0, 1, 1, 3, 3,
        },
        new int[] { 0, 1, 3, 5, 6, 7 },
        new int[] { 4, 5 }),
    };

    private static readonly SBox[] Fn1R2Boxes =
    {
        new(new byte[]
        {
            3, 3, 2, 0, 3, 0, 3, 1, 0, 3, 0, 1, 0, 2, 1, 3,
            1, 3, 0, 3, 3, 1, 3, 3, 3, 2, 3, 2, 2, 3, 1, 2,
            0, 2, 2, 1, 0, 1, 2, 0, 3, 3, 0, 1, 3, 2, 1, 2,
            3, 0, 1, 3, 0, 1, 2, 2, 1, 2, 1, 2, 0, 1, 3, 0,
        },
        new int[] { 0, 1, 2, 3, 6, -1 },
        new int[] { 1, 6 }),
        new(new byte[]
        {
            1, 2, 3, 2, 1, 3, 0, 1, 1, 0, 2, 0, 0, 2, 3, 2,
            3, 3, 0, 1, 2, 2, 1, 0, 1, 0, 1, 2, 3, 2, 1, 3,
            2, 2, 2, 0, 1, 0, 2, 3, 2, 1, 2, 1, 2, 1, 0, 3,
            0, 1, 2, 3, 1, 2, 1, 3, 2, 0, 3, 2, 3, 0, 2, 0,
        },
        new int[] { 2, 4, 5, 6, 7, -1 },
        new int[] { 5, 7 }),
        new(new byte[]
        {
            0, 1, 0, 2, 1, 1, 0, 1, 0, 2, 2, 2, 1, 3, 0, 0,
            1, 1, 3, 1, 2, 2, 2, 3, 1, 0, 3, 3, 3, 2, 2, 2,
            1, 1, 3, 0, 3, 1, 3, 0, 1, 3, 3, 2, 1, 1, 0, 0,
            1, 2, 2, 2, 1, 1, 1, 2, 2, 0, 0, 3, 2, 3, 1, 3,
        },
        new int[] { 1, 2, 3, 4, 5, 7 },
        new int[] { 0, 3 }),
        new(new byte[]
        {
            2, 1, 0, 3, 3, 3, 2, 0, 1, 2, 1, 1, 1, 0, 3, 1,
            1, 3, 3, 0, 1, 2, 1, 0, 0, 0, 3, 0, 3, 0, 3, 0,
            1, 3, 3, 3, 0, 3, 2, 0, 2, 1, 2, 2, 2, 1, 1, 3,
            0, 1, 0, 1, 0, 1, 1, 1, 1, 3, 1, 0, 1, 2, 3, 3,
        },
        new int[] { 0, 1, 3, 4, 6, 7 },
        new int[] { 2, 4 }),
    };

    private static readonly SBox[] Fn1R3Boxes =
    {
        new(new byte[]
        {
            0, 0, 0, 3, 3, 1, 1, 0, 2, 0, 2, 0, 0, 0, 3, 2,
            0, 1, 2, 3, 2, 2, 1, 0, 3, 0, 0, 0, 0, 0, 2, 3,
            3, 0, 0, 1, 1, 2, 3, 3, 0, 1, 3, 2, 0, 1, 3, 3,
            2, 0, 0, 1, 0, 2, 0, 0, 0, 3, 1, 3, 3, 3, 3, 3,
        },
        new int[] { 0, 1, 5, 6, 7, -1 },
        new int[] { 0, 5 }),
        new(new byte[]
        {
            2, 3, 2, 3, 0, 2, 3, 0, 2, 2, 3, 0, 3, 2, 0, 2,
            1, 0, 2, 3, 1, 1, 1, 0, 0, 1, 0, 2, 1, 2, 2, 1,
            3, 0, 2, 1, 2, 3, 3, 0, 3, 2, 3, 1, 0, 2, 1, 0,
            1, 2, 2, 3, 0, 2, 1, 3, 1, 3, 0, 2, 1, 1, 1, 3,
        },
        new int[] { 2, 3, 4, 6, 7, -1 },
        new int[] { 6, 7 }),
        new(new byte[]
        {
            3, 0, 2, 1, 1, 3, 1, 2, 2, 1, 2, 2, 2, 0, 0, 1,
            2, 3, 1, 0, 2, 0, 0, 2, 3, 1, 2, 0, 0, 0, 3, 0,
            2, 1, 1, 2, 0, 0, 1, 2, 3, 1, 1, 2, 0, 1, 3, 0,
            3, 1, 1, 0, 0, 2, 3, 0, 0, 0, 0, 3, 2, 0, 0, 0,
        },
        new int[] { 0, 2, 3, 4, 5, 6 },
        new int[] { 1, 4 }),
        new(new byte[]
        {
            0, 1, 0, 0, 2, 1, 3, 2, 3, 3, 2, 1, 0, 1, 1, 1,
            1, 1, 0, 3, 3, 1, 1, 0, 0, 2, 2, 1, 0, 3, 3, 2,
            1, 3, 3, 0, 3, 0, 2, 1, 1, 2, 3, 2, 2, 2, 1, 0,
            0, 3, 3, 3, 2, 2, 3, 1, 0, 2, 3, 0, 3, 1, 1, 0,
        },
        new int[] { 0, 1, 2, 3, 5, 7 },
        new int[] { 2, 3 }),
    };

    private static readonly SBox[] Fn1R4Boxes =
    {
        new(new byte[]
        {
            1, 1, 1, 1, 1, 0, 1, 3, 3, 2, 3, 0, 1, 2, 0, 2,
            3, 3, 0, 1, 2, 1, 2, 3, 0, 3, 2, 3, 2, 0, 1, 2,
            0, 1, 0, 3, 2, 1, 3, 2, 3, 1, 2, 3, 2, 0, 1, 2,
            2, 0, 0, 0, 2, 1, 3, 0, 3, 1, 3, 0, 1, 3, 3, 0,
        },
        new int[] { 1, 2, 3, 4, 5, 7 },
        new int[] { 0, 4 }),
        new(new byte[]
        {
            3, 0, 0, 0, 0, 1, 0, 2, 3, 3, 1, 3, 0, 3, 1, 2,
            2, 2, 3, 1, 0, 0, 2, 0, 1, 0, 2, 2, 3, 3, 0, 0,
            1, 1, 3, 0, 2, 3, 0, 3, 0, 3, 0, 2, 0, 2, 0, 1,
            0, 3, 0, 1, 3, 1, 1, 0, 0, 1, 3, 3, 2, 2, 1, 0,
        },
        new int[] { 0, 1, 2, 3, 5, 6 },
        new int[] { 1, 3 }),
        new(new byte[]
        {
            0, 1, 1, 2, 0, 1, 3, 1, 2, 0, 3, 2, 0, 0, 3, 0,
            3, 0, 1, 2, 2, 3, 3, 2, 3, 2, 0, 1, 0, 0, 1, 0,
            3, 0, 2, 3, 0, 2, 2, 2, 1, 1, 0, 2, 2, 0, 0, 1,
            2, 1, 1, 1, 2, 3, 0, 3, 1, 2, 3, 3, 1, 1, 3, 0,
        },
        new int[] { 0, 2, 4, 5, 6, 7 },
        new int[] { 2, 6 }),
        new(new byte[]
        {
            0, 1, 2, 2, 0, 1, 0, 3, 2, 2, 1, 1, 3, 2, 0, 2,
            0, 1, 3, 3, 0, 2, 2, 3, 3, 2, 0, 0, 2, 1, 3, 3,
            1, 1, 1, 3, 1, 2, 1, 1, 0, 3, 3, 2, 3, 2, 3, 0,
            3, 1, 0, 0, 3, 0, 0, 0, 2, 2, 2, 1, 2, 3, 0, 0,
        },
        new int[] { 0, 1, 3, 4, 6, 7 },
        new int[] { 5, 7 }),
    };

    private static readonly SBox[] Fn2R1Boxes =
    {
        new(new byte[]
        {
            2, 0, 2, 0, 3, 0, 0, 3, 1, 1, 0, 1, 3, 2, 0, 1,
            2, 0, 1, 2, 0, 2, 0, 2, 2, 2, 3, 0, 2, 1, 3, 0,
            0, 1, 0, 1, 2, 2, 3, 3, 0, 3, 0, 2, 3, 0, 1, 2,
            1, 1, 0, 2, 0, 3, 1, 1, 2, 2, 1, 3, 1, 1, 3, 1,
        },
        new int[] { 0, 3, 4, 5, 7, -1 },
        new int[] { 6, 7 }),
        new(new byte[]
        {
            1, 1, 0, 3, 0, 2, 0, 1, 3, 0, 2, 0, 1, 1, 0, 0,
            1, 3, 2, 2, 0, 2, 2, 2, 2, 0, 1, 3, 3, 3, 1, 1,
            1, 3, 1, 3, 2, 2, 2, 2, 2, 2, 0, 1, 0, 1, 1, 2,
            3, 1, 1, 2, 0, 3, 3, 3, 2, 2, 3, 1, 1, 1, 3, 0,
        },
        new int[] { 1, 2, 3, 4, 6, -1 },
        new int[] { 3, 5 }),
        new(new byte[]
        {
            1, 0, 2, 2, 3, 3, 3, 3, 1, 2, 2, 1, 0, 1, 2, 1,
            1, 2, 3, 1, 2, 0, 0, 1, 2, 3, 1, 2, 0, 0, 0, 2,
            2, 0, 1, 1, 0, 0, 2, 0, 0, 0, 2, 3, 2, 3, 0, 1,
            3, 0, 0, 0, 2, 3, 2, 0, 1, 3, 2, 1, 3, 1, 1, 3,
        },
        new int[] { 1, 2, 4, 5, 6, 7 },
        new int[] { 1, 4 }),
        new(new byte[]
        {
            1, 3, 3, 0, 3, 2, 3, 1, 3, 2, 1, 1, 3, 3, 2, 1,
            2, 3, 0, 3, 1, 0, 0, 2, 3, 0, 0, 0, 3, 3, 0, 1,
            2, 3, 0, 0, 0, 1, 2, 1, 3, 0, 0, 1, 0, 2, 2, 2,
            3, 3, 1, 2, 1, 3, 0, 0, 0, 3, 0, 1, 3, 2, 2, 0,
        },
        new int[] { 0, 2, 3, 5, 6, 7 },
        new int[] { 0, 2 }),
    };

    private static readonly SBox[] Fn2R2Boxes =
    {
        new(new byte[]
        {
            3, 1, 3, 0, 3, 0, 3, 1, 3, 0, 0, 1, 1, 3, 0, 3,
            1, 1, 0, 1, 2, 3, 2, 3, 3, 1, 2, 2, 2, 0, 2, 3,
            2, 2, 2, 1, 1, 3, 3, 0, 3, 1, 2, 1, 1, 1, 0, 2,
            0, 3, 3, 0, 0, 2, 0, 0, 1, 1, 2, 1, 2, 1, 1, 0,
        },
        new int[] { 0, 2, 4, 6, -1, -1 },
        new int[] { 4, 6 }),
        new(new byte[]
        {
            0, 3, 0, 3, 3, 2, 1, 2, 3, 1, 1, 1, 2, 0, 2, 3,
            0, 3, 1, 2, 2, 1, 3, 3, 3, 2, 1, 2, 2, 0, 1, 0,
            2, 3, 0, 1, 2, 0, 1, 1, 2, 0, 2, 1, 2, 0, 2, 3,
            3, 1, 0, 2, 3, 3, 0, 3, 1, 1, 3, 0, 0, 1, 2, 0,
        },
        new int[] { 1, 3, 4, 5, 6, 7 },
        new int[] { 0, 3 }),
        new(new byte[]
        {
            0, 0, 2, 1, 3, 2, 1, 0, 1, 2, 2, 2, 1, 1, 0, 3,
            1, 2, 2, 3, 2, 1, 1, 0, 3, 0, 0, 1, 1, 2, 3, 1,
            3, 3, 2, 2, 1, 0, 1, 1, 1, 2, 0, 1, 2, 3, 0, 3,
            3, 0, 3, 2, 2, 0, 2, 2, 1, 2, 3, 2, 1, 0, 2, 1,
        },
        new int[] { 0, 1, 3, 4, 5, 7 },
        new int[] { 1, 7 }),
        new(new byte[]
        {
            0, 2, 1, 2, 0, 2, 2, 0, 1, 3, 2, 0, 3, 2, 3, 0,
            3, 3, 2, 3, 1, 2, 3, 1, 2, 2, 0, 0, 2, 2, 1, 2,
            2, 3, 3, 3, 1, 1, 0, 0, 0, 3, 2, 0, 3, 2, 3, 1,
            1, 1, 1, 0, 1, 0, 1, 3, 0, 0, 1, 2, 2, 3, 2, 0,
        },
        new int[] { 1, 2, 3, 5, 6, 7 },
        new int[] { 2, 5 }),
    };

    private static readonly SBox[] Fn2R3Boxes =
    {
        new(new byte[]
        {
            2, 1, 2, 1, 2, 3, 1, 3, 2, 2, 1, 3, 3, 0, 0, 1,
            0, 2, 0, 3, 3, 1, 0, 0, 1, 1, 0, 2, 3, 2, 1, 2,
            1, 1, 2, 1, 1, 3, 2, 2, 0, 2, 2, 3, 3, 3, 2, 0,
            0, 0, 0, 0, 3, 3, 3, 0, 1, 2, 1, 0, 2, 3, 3, 1,
        },
        new int[] { 2, 3, 4, 6, -1, -1 },
        new int[] { 3, 5 }),
        new(new byte[]
        {
            3, 2, 3, 3, 1, 0, 3, 0, 2, 0, 1, 1, 1, 0, 3, 0,
            3, 1, 3, 1, 0, 1, 2, 3, 2, 2, 3, 2, 0, 1, 1, 2,
            3, 0, 0, 2, 1, 0, 0, 2, 2, 0, 1, 0, 0, 2, 0, 0,
            1, 3, 1, 3, 2, 0, 3, 3, 1, 0, 2, 2, 2, 3, 0, 0,
        },
        new int[] { 0, 1, 3, 5, 7, -1 },
        new int[] { 0, 2 }),
        new(new byte[]
        {
            2, 2, 1, 0, 2, 3, 3, 0, 0, 0, 1, 3, 1, 2, 3, 2,
            2, 3, 1, 3, 0, 3, 0, 3, 3, 2, 2, 1, 0, 0, 0, 2,
            1, 2, 2, 2, 0, 0, 1, 2, 0, 1, 3, 0, 2, 3, 2, 1,
            3, 2, 2, 2, 3, 1, 3, 0, 2, 0, 2, 1, 0, 3, 3, 1,
        },
        new int[] { 0, 1, 2, 3, 5, 7 },
        new int[] { 1, 6 }),
        new(new byte[]
        {
            1, 2, 3, 2, 0, 2, 1, 3, 3, 1, 0, 1, 1, 2, 2, 0,
            0, 1, 1, 1, 2, 1, 1, 2, 0, 1, 3, 3, 1, 1, 1, 2,
            3, 3, 1, 0, 2, 1, 1, 1, 2, 1, 0, 0, 2, 2, 3, 2,
            3, 2, 2, 0, 2, 2, 3, 3, 0, 2, 3, 0, 2, 2, 1, 1,
        },
        new int[] { 0, 2, 4, 5, 6, 7 },
        new int[] { 4, 7 }),
    };

    private static readonly SBox[] Fn2R4Boxes =
    {
        new(new byte[]
        {
            2, 0, 1, 1, 2, 1, 3, 3, 1, 1, 1, 2, 0, 1, 0, 2,
            0, 1, 2, 0, 2, 3, 0, 2, 3, 3, 2, 2, 3, 2, 0, 1,
            3, 0, 2, 0, 2, 3, 1, 3, 2, 0, 0, 1, 1, 2, 3, 1,
            1, 1, 0, 1, 2, 0, 3, 3, 1, 1, 1, 3, 3, 1, 1, 0,
        },
        new int[] { 0, 1, 3, 6, 7, -1 },
        new int[] { 0, 3 }),
        new(new byte[]
        {
            1, 2, 2, 1, 0, 3, 3, 1, 0, 2, 2, 2, 1, 0, 1, 0,
            1, 1, 0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 3, 2, 3, 3,
            2, 2, 1, 2, 2, 3, 1, 3, 3, 3, 0, 1, 0, 1, 3, 0,
            0, 0, 1, 2, 0, 3, 3, 2, 3, 2, 1, 3, 2, 1, 0, 2,
        },
        new int[] { 0, 1, 2, 4, 5, 6 },
        new int[] { 4, 7 }),
        new(new byte[]
        {
            2, 3, 2, 1, 3, 2, 3, 0, 0, 2, 1, 1, 0, 0, 3, 2,
            3, 1, 0, 1, 2, 2, 2, 1, 3, 2, 2, 1, 0, 2, 1, 2,
            0, 3, 1, 0, 0, 3, 1, 1, 3, 3, 2, 0, 1, 0, 1, 3,
            0, 0, 1, 2, 1, 2, 3, 2, 1, 0, 0, 3, 2, 1, 1, 3,
        },
        new int[] { 0, 2, 3, 4, 5, 7 },
        new int[] { 1, 2 }),
        new(new byte[]
        {
            2, 0, 0, 3, 2, 2, 2, 1, 3, 3, 1, 1, 2, 0, 0, 3,
            1, 0, 3, 2, 1, 0, 2, 0, 3, 2, 2, 3, 2, 0, 3, 0,
            1, 3, 0, 2, 2, 1, 3, 3, 0, 1, 0, 3, 1, 1, 3, 2,
            0, 3, 0, 2, 3, 2, 1, 3, 2, 3, 0, 0, 1, 3, 2, 1,
        },
        new int[] { 2, 3, 4, 5, 6, 7 },
        new int[] { 5, 6 }),
    };


    public static byte[] Decrypt(ReadOnlySpan<byte> encryptedProgram, ReadOnlySpan<byte> keyRegion)
    {
        if (keyRegion.Length < 20)
            throw new InvalidOperationException("CPS2 key region must contain at least 20 bytes.");

        ushort[] decoded = DecodeKey(keyRegion);
        uint[] key =
        {
            ((uint)decoded[0] << 16) | decoded[1],
            ((uint)decoded[2] << 16) | decoded[3]
        };

        uint lower;
        uint upper;
        if (decoded[9] == 0xffff)
        {
            lower = 0xff0000;
            upper = 0xffffff;
        }
        else
        {
            lower = 0;
            upper = (((uint)(~decoded[9] & 0x03ff) << 14) | 0x3fff) + 1;
        }

        byte[] decrypted = encryptedProgram.ToArray();
        DecryptWords(encryptedProgram, decrypted, key, lower / 2, upper / 2);
        return decrypted;
    }

    private static ushort[] DecodeKey(ReadOnlySpan<byte> keyRegion)
    {
        ushort[] decoded = new ushort[10];
        for (int b = 0; b < 10 * 16; b++)
        {
            int bit = (317 - b) % 160;
            if (((keyRegion[bit / 8] >> ((bit ^ 7) % 8)) & 1) != 0)
                decoded[b / 16] |= (ushort)(0x8000 >> (b % 16));
        }

        return decoded;
    }

    private static void DecryptWords(ReadOnlySpan<byte> rom, Span<byte> dec, uint[] masterKey, uint lowerLimit, uint upperLimit)
    {
        OptimizedSBox[] sboxes10 = Optimize(Fn1R1Boxes);
        OptimizedSBox[] sboxes11 = Optimize(Fn1R2Boxes);
        OptimizedSBox[] sboxes12 = Optimize(Fn1R3Boxes);
        OptimizedSBox[] sboxes13 = Optimize(Fn1R4Boxes);
        OptimizedSBox[] sboxes20 = Optimize(Fn2R1Boxes);
        OptimizedSBox[] sboxes21 = Optimize(Fn2R2Boxes);
        OptimizedSBox[] sboxes22 = Optimize(Fn2R3Boxes);
        OptimizedSBox[] sboxes23 = Optimize(Fn2R4Boxes);

        uint[] key1 = new uint[4];
        ExpandFirstKey(key1, masterKey);
        key1[0] ^= (uint)Bit(key1[0], 1) << 4;
        key1[0] ^= (uint)Bit(key1[0], 2) << 5;
        key1[0] ^= (uint)Bit(key1[0], 8) << 11;
        key1[1] ^= (uint)Bit(key1[1], 0) << 5;
        key1[1] ^= (uint)Bit(key1[1], 8) << 11;
        key1[2] ^= (uint)Bit(key1[2], 1) << 5;
        key1[2] ^= (uint)Bit(key1[2], 8) << 11;

        int wordLength = rom.Length / 2;
        for (int i = 0; i < 0x10000; i++)
        {
            ushort seed = Feistel((ushort)i, Fn1GroupA, Fn1GroupB, sboxes10, sboxes11, sboxes12, sboxes13, key1[0], key1[1], key1[2], key1[3]);
            uint[] subkey = new uint[2];
            ExpandSubkey(subkey, seed);
            subkey[0] ^= masterKey[0];
            subkey[1] ^= masterKey[1];

            uint[] key2 = new uint[4];
            ExpandSecondKey(key2, subkey);
            key2[0] ^= (uint)Bit(key2[0], 0) << 5;
            key2[0] ^= (uint)Bit(key2[0], 6) << 11;
            key2[1] ^= (uint)Bit(key2[1], 0) << 5;
            key2[1] ^= (uint)Bit(key2[1], 1) << 4;
            key2[2] ^= (uint)Bit(key2[2], 2) << 5;
            key2[2] ^= (uint)Bit(key2[2], 3) << 4;
            key2[2] ^= (uint)Bit(key2[2], 7) << 11;
            key2[3] ^= (uint)Bit(key2[3], 1) << 5;

            for (int a = i; a < wordLength; a += 0x10000)
            {
                ushort word = ReadBigEndianWord(rom, a * 2);
                ushort decoded = ((uint)a >= lowerLimit && (uint)a <= upperLimit)
                    ? Feistel(word, Fn2GroupA, Fn2GroupB, sboxes20, sboxes21, sboxes22, sboxes23, key2[0], key2[1], key2[2], key2[3])
                    : word;
                WriteBigEndianWord(dec, a * 2, decoded);
            }
        }
    }

    private static OptimizedSBox[] Optimize(SBox[] boxes)
    {
        OptimizedSBox[] optimized = new OptimizedSBox[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
            optimized[i] = new OptimizedSBox(boxes[i]);
        return optimized;
    }

    private static int Fn(byte value, OptimizedSBox[] boxes, uint key)
        => boxes[0].Fn(value, key >> 0)
           | boxes[1].Fn(value, key >> 6)
           | boxes[2].Fn(value, key >> 12)
           | boxes[3].Fn(value, key >> 18);

    private static void ExpandFirstKey(uint[] dstKey, uint[] srcKey)
    {
        int[] bits =
        {
            33, 58, 49, 36, 0, 31, 22, 30, 3, 16, 5, 53,
            10, 41, 23, 19, 27, 39, 43, 6, 34, 12, 61, 21,
            48, 13, 32, 35, 6, 42, 43, 14, 21, 41, 52, 25,
            18, 47, 46, 37, 57, 53, 20, 8, 55, 54, 59, 60,
            27, 33, 35, 18, 8, 15, 63, 1, 50, 44, 16, 46,
            5, 4, 45, 51, 38, 25, 13, 11, 62, 29, 48, 2,
            59, 61, 62, 56, 51, 57, 54, 9, 24, 63, 22, 7,
            26, 42, 45, 40, 23, 14, 2, 31, 52, 28, 44, 17,
        };

        Array.Clear(dstKey);
        for (int i = 0; i < 96; i++)
            dstKey[i / 24] |= (uint)Bit(srcKey[bits[i] / 32], bits[i] % 32) << (i % 24);
    }

    private static void ExpandSecondKey(uint[] dstKey, uint[] srcKey)
    {
        int[] bits =
        {
            34, 9, 32, 24, 44, 54, 38, 61, 47, 13, 28, 7,
            29, 58, 18, 1, 20, 60, 15, 6, 11, 43, 39, 19,
            63, 23, 16, 62, 54, 40, 31, 3, 56, 61, 17, 25,
            47, 38, 55, 57, 5, 4, 15, 42, 22, 7, 2, 19,
            46, 37, 29, 39, 12, 30, 49, 57, 31, 41, 26, 27,
            24, 36, 11, 63, 33, 16, 56, 62, 48, 60, 59, 32,
            12, 30, 53, 48, 10, 0, 50, 35, 3, 59, 14, 49,
            51, 45, 44, 2, 21, 33, 55, 52, 23, 28, 8, 26,
        };

        Array.Clear(dstKey);
        for (int i = 0; i < 96; i++)
            dstKey[i / 24] |= (uint)Bit(srcKey[bits[i] / 32], bits[i] % 32) << (i % 24);
    }

    private static void ExpandSubkey(uint[] subkey, ushort seed)
    {
        int[] bits =
        {
            5, 10, 14, 9, 4, 0, 15, 6, 1, 8, 3, 2, 12, 7, 13, 11,
            5, 12, 7, 2, 13, 11, 9, 14, 4, 1, 6, 10, 8, 0, 15, 3,
            4, 10, 2, 0, 6, 9, 12, 1, 11, 7, 15, 8, 13, 5, 14, 3,
            14, 11, 12, 7, 4, 5, 2, 10, 1, 15, 0, 9, 8, 6, 13, 3,
        };

        Array.Clear(subkey);
        for (int i = 0; i < 64; i++)
            subkey[i / 32] |= (uint)Bit(seed, bits[i]) << (i % 32);
    }

    private static ushort Feistel(ushort value, int[] bitsA, int[] bitsB, OptimizedSBox[] boxes1, OptimizedSBox[] boxes2, OptimizedSBox[] boxes3, OptimizedSBox[] boxes4, uint key1, uint key2, uint key3, uint key4)
    {
        byte l = Bitswap8(value, bitsB);
        byte r = Bitswap8(value, bitsA);

        l ^= (byte)Fn(r, boxes1, key1);
        r ^= (byte)Fn(l, boxes2, key2);
        l ^= (byte)Fn(r, boxes3, key3);
        r ^= (byte)Fn(l, boxes4, key4);

        int result = 0;
        for (int i = 0; i < 8; i++)
        {
            result |= Bit(l, i) << bitsA[i];
            result |= Bit(r, i) << bitsB[i];
        }

        return (ushort)result;
    }

    private static byte Bitswap8(ushort value, int[] bits)
    {
        int result = 0;
        for (int i = 0; i < 8; i++)
            result |= Bit(value, bits[i]) << i;
        return (byte)result;
    }

    private static int Bit(uint value, int bit) => (int)((value >> bit) & 1u);

    private static int Bit(ushort value, int bit) => (value >> bit) & 1;

    private static int Bit(byte value, int bit) => (value >> bit) & 1;

    private static ushort ReadBigEndianWord(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteBigEndianWord(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private readonly struct SBox
    {
        public readonly byte[] Table;
        public readonly int[] Inputs;
        public readonly int[] Outputs;

        public SBox(byte[] table, int[] inputs, int[] outputs)
        {
            Table = table;
            Inputs = inputs;
            Outputs = outputs;
        }

        public int ExtractInputs(int value)
        {
            int result = 0;
            for (int i = 0; i < 6; i++)
            {
                if (Inputs[i] >= 0)
                    result |= Bit((uint)value, Inputs[i]) << i;
            }

            return result;
        }
    }

    private sealed class OptimizedSBox
    {
        private readonly byte[] _inputLookup = new byte[256];
        private readonly byte[] _output = new byte[64];

        public OptimizedSBox(SBox source)
        {
            for (int i = 0; i < _inputLookup.Length; i++)
                _inputLookup[i] = (byte)source.ExtractInputs(i);

            for (int i = 0; i < _output.Length; i++)
            {
                int output = source.Table[i];
                if ((output & 1) != 0)
                    _output[i] |= (byte)(1 << source.Outputs[0]);
                if ((output & 2) != 0)
                    _output[i] |= (byte)(1 << source.Outputs[1]);
            }
        }

        public int Fn(byte input, uint key)
            => _output[_inputLookup[input] ^ (key & 0x3f)];
    }
}
