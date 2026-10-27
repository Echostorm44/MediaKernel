using System;
using System.Runtime.CompilerServices;

namespace MediaKernel.Core.Codecs.H264
{
    /// <summary>
    /// CAVLC (Context-Adaptive Variable Length Coding) decoder for H.264/AVC.
    /// Used in Baseline and Main profiles when entropy_coding_mode_flag is 0.
    /// VLC tables from ITU-T H.264/AVC Tables 9-5 through 9-10.
    /// </summary>
    public ref struct CavlcDecoder
    {
        private BitstreamReader reader;

        #region Static VLC Tables (ITU-T H.264 spec)

        // coeff_token tables (Table 9-5). Indexed as [trailingOnes + totalCoeffs * 4].
        // 4 tables x 68 entries each. len == 0 marks invalid/unused entries.

        private static readonly byte[] CoeffTokenLen0 = // nC 0-1
        {
             1, 0, 0, 0,
             6, 2, 0, 0,     8, 6, 3, 0,     9, 8, 7, 5,    10, 9, 8, 6,
            11,10, 9, 7,    13,11,10, 8,    13,13,11, 9,    13,13,13,10,
            14,14,13,11,    14,14,14,13,    15,15,14,14,    15,15,15,14,
            16,15,15,15,    16,16,16,15,    16,16,16,16,    16,16,16,16,
        };
        private static readonly byte[] CoeffTokenBits0 =
        {
             1, 0, 0, 0,
             5, 1, 0, 0,     7, 4, 1, 0,     7, 6, 5, 3,     7, 6, 5, 3,
             7, 6, 5, 4,    15, 6, 5, 4,    11,14, 5, 4,     8,10,13, 4,
            15,14, 9, 4,    11,10,13,12,    15,14, 9,12,    11,10,13, 8,
            15, 1, 9,12,    11,14,13, 8,     7,10, 9,12,     4, 6, 5, 8,
        };

        private static readonly byte[] CoeffTokenLen1 = // nC 2-3
        {
             2, 0, 0, 0,
             6, 2, 0, 0,     6, 5, 3, 0,     7, 6, 6, 4,     8, 6, 6, 4,
             8, 7, 7, 5,     9, 8, 8, 6,    11, 9, 9, 6,    11,11,11, 7,
            12,11,11, 9,    12,12,12,11,    12,12,12,11,    13,13,13,12,
            13,13,13,13,    13,14,13,13,    14,14,14,13,    14,14,14,14,
        };
        private static readonly byte[] CoeffTokenBits1 =
        {
             3, 0, 0, 0,
            11, 2, 0, 0,     7, 7, 3, 0,     7,10, 9, 5,     7, 6, 5, 4,
             4, 6, 5, 6,     7, 6, 5, 8,    15, 6, 5, 4,    11,14,13, 4,
            15,10, 9, 4,    11,14,13,12,     8,10, 9, 8,    15,14,13,12,
            11,10, 9,12,     7,11, 6, 8,     9, 8,10, 1,     7, 6, 5, 4,
        };

        private static readonly byte[] CoeffTokenLen2 = // nC 4-7
        {
             4, 0, 0, 0,
             6, 4, 0, 0,     6, 5, 4, 0,     6, 5, 5, 4,     7, 5, 5, 4,
             7, 5, 5, 4,     7, 6, 6, 4,     7, 6, 6, 4,     8, 7, 7, 5,
             8, 8, 7, 6,     9, 8, 8, 7,     9, 9, 8, 8,     9, 9, 9, 8,
            10, 9, 9, 9,    10,10,10,10,    10,10,10,10,    10,10,10,10,
        };
        private static readonly byte[] CoeffTokenBits2 =
        {
            15, 0, 0, 0,
            15,14, 0, 0,    11,15,13, 0,     8,12,14,12,    15,10,11,11,
            11, 8, 9,10,     9,14,13, 9,     8,10, 9, 8,    15,14,13,13,
            11,14,10,12,    15,10,13,12,    11,14, 9,12,     8,10,13, 8,
            13, 7, 9,12,     9,12,11,10,     5, 8, 7, 6,     1, 4, 3, 2,
        };

        private static readonly byte[] CoeffTokenLen3 = // nC >= 8 (fixed 6-bit codes)
        {
             6, 0, 0, 0,
             6, 6, 0, 0,     6, 6, 6, 0,     6, 6, 6, 6,     6, 6, 6, 6,
             6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,
             6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,
             6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,     6, 6, 6, 6,
        };
        private static readonly byte[] CoeffTokenBits3 =
        {
             3, 0, 0, 0,
             0, 1, 0, 0,     4, 5, 6, 0,     8, 9,10,11,    12,13,14,15,
            16,17,18,19,    20,21,22,23,    24,25,26,27,    28,29,30,31,
            32,33,34,35,    36,37,38,39,    40,41,42,43,    44,45,46,47,
            48,49,50,51,    52,53,54,55,    56,57,58,59,    60,61,62,63,
        };

        private static readonly int[] CoeffTokenMaxLen = { 16, 14, 10, 6 };

        // Chroma DC coeff_token (Table 9-5, nC = -1). 5 rows x 4 columns = 20 entries.
        private static readonly byte[] ChromaDcCoeffTokenLen =
        {
             2, 0, 0, 0,
             6, 1, 0, 0,
             6, 6, 3, 0,
             6, 7, 7, 6,
             6, 8, 8, 7,
        };
        private static readonly byte[] ChromaDcCoeffTokenBits =
        {
             1, 0, 0, 0,
             7, 1, 0, 0,
             4, 6, 1, 0,
             3, 3, 2, 5,
             2, 3, 2, 0,
        };
        private const int ChromaDcCoeffTokenMaxLen = 8;

        // Chroma 4:2:2 DC coeff_token (Table 9-5, nC = -2). 9 rows x 4 columns = 36 entries.
        private static readonly byte[] Chroma422DcCoeffTokenLen =
        {
             1,  0,  0,  0,
             7,  2,  0,  0,
             7,  7,  3,  0,
             9,  7,  7,  5,
             9,  9,  7,  6,
            10, 10,  9,  7,
            11, 11, 10,  7,
            12, 12, 11, 10,
            13, 12, 12, 11,
        };
        private static readonly byte[] Chroma422DcCoeffTokenBits =
        {
             1,  0,  0,  0,
            15,  1,  0,  0,
            14, 13,  1,  0,
             7, 12, 11,  1,
             6,  5, 10,  1,
             7,  6,  4,  9,
             7,  6,  5,  8,
             7,  6,  5,  4,
             7,  5,  4,  4,
        };
        private const int Chroma422DcCoeffTokenMaxLen = 13;

        // total_zeros tables (Table 9-7). 15 rows indexed by totalCoeffs-1.
        private static readonly byte[][] TotalZerosLen =
        {
            new byte[] {1,3,3,4,4,5,5,6,6,7,7,8,8,9,9,9},
            new byte[] {3,3,3,3,3,4,4,4,4,5,5,6,6,6,6},
            new byte[] {4,3,3,3,4,4,3,3,4,5,5,6,5,6},
            new byte[] {5,3,4,4,3,3,3,4,3,4,5,5,5},
            new byte[] {4,4,4,3,3,3,3,3,4,5,4,5},
            new byte[] {6,5,3,3,3,3,3,3,4,3,6},
            new byte[] {6,5,3,3,3,2,3,4,3,6},
            new byte[] {6,4,5,3,2,2,3,3,6},
            new byte[] {6,6,4,2,2,3,2,5},
            new byte[] {5,5,3,2,2,2,4},
            new byte[] {4,4,3,3,1,3},
            new byte[] {4,4,2,1,3},
            new byte[] {3,3,1,2},
            new byte[] {2,2,1},
            new byte[] {1,1},
        };
        private static readonly byte[][] TotalZerosBits =
        {
            new byte[] {1,3,2,3,2,3,2,3,2,3,2,3,2,3,2,1},
            new byte[] {7,6,5,4,3,5,4,3,2,3,2,3,2,1,0},
            new byte[] {5,7,6,5,4,3,4,3,2,3,2,1,1,0},
            new byte[] {3,7,5,4,6,5,4,3,3,2,2,1,0},
            new byte[] {5,4,3,7,6,5,4,3,2,1,1,0},
            new byte[] {1,1,7,6,5,4,3,2,1,1,0},
            new byte[] {1,1,5,4,3,3,2,1,1,0},
            new byte[] {1,1,1,3,3,2,2,1,0},
            new byte[] {1,0,1,3,2,1,1,1},
            new byte[] {1,0,1,3,2,1,1},
            new byte[] {0,1,1,2,1,3},
            new byte[] {0,1,1,1,1},
            new byte[] {0,1,1,1},
            new byte[] {0,1,1},
            new byte[] {0,1},
        };
        private static readonly byte[] TotalZerosMaxLen = { 9, 6, 6, 5, 5, 6, 6, 6, 6, 5, 4, 4, 3, 2, 1 };

        // Chroma DC total_zeros (Table 9-9). 3 rows indexed by totalCoeffs-1.
        private static readonly byte[][] ChromaDcTotalZerosLen =
        {
            new byte[] {1, 2, 3, 3},
            new byte[] {1, 2, 2, 0},
            new byte[] {1, 1, 0, 0},
        };
        private static readonly byte[][] ChromaDcTotalZerosBits =
        {
            new byte[] {1, 1, 1, 0},
            new byte[] {1, 1, 0, 0},
            new byte[] {1, 0, 0, 0},
        };
        private static readonly byte[] ChromaDcTotalZerosMaxLen = { 3, 2, 1 };

        // Chroma 4:2:2 DC total_zeros (Table 9-9a). 7 rows indexed by totalCoeffs-1.
        private static readonly byte[][] Chroma422DcTotalZerosLen =
        {
            new byte[] {1, 3, 3, 4, 4, 4, 5, 5},
            new byte[] {3, 2, 3, 3, 3, 3, 3},
            new byte[] {3, 3, 2, 2, 3, 3},
            new byte[] {3, 2, 2, 2, 3},
            new byte[] {2, 2, 2, 2},
            new byte[] {2, 2, 1},
            new byte[] {1, 1},
        };
        private static readonly byte[][] Chroma422DcTotalZerosBits =
        {
            new byte[] {1, 2, 3, 2, 3, 1, 1, 0},
            new byte[] {0, 1, 1, 4, 5, 6, 7},
            new byte[] {0, 1, 1, 2, 6, 7},
            new byte[] {6, 0, 1, 2, 7},
            new byte[] {0, 1, 2, 3},
            new byte[] {0, 1, 1},
            new byte[] {0, 1},
        };
        private static readonly byte[] Chroma422DcTotalZerosMaxLen = { 5, 3, 3, 3, 2, 2, 1 };

        // run_before tables (Table 9-10). 7 rows indexed by min(zerosLeft-1, 6).
        private static readonly byte[][] RunBeforeLen =
        {
            new byte[] {1,1},
            new byte[] {1,2,2},
            new byte[] {2,2,2,2},
            new byte[] {2,2,2,3,3},
            new byte[] {2,2,3,3,3,3},
            new byte[] {2,3,3,3,3,3,3},
            new byte[] {3,3,3,3,3,3,3,4,5,6,7,8,9,10,11},
        };
        private static readonly byte[][] RunBeforeBits =
        {
            new byte[] {1,0},
            new byte[] {1,1,0},
            new byte[] {3,2,1,0},
            new byte[] {3,2,1,1,0},
            new byte[] {3,2,3,2,1,0},
            new byte[] {3,0,1,3,2,5,4},
            new byte[] {7,6,5,4,3,2,1,1,1,1,1,1,1,1,1},
        };
        private static readonly byte[] RunBeforeMaxLen = { 1, 2, 2, 3, 3, 3, 11 };

        #endregion

        /// <summary>
        /// Creates a new CAVLC decoder.
        /// </summary>
        public CavlcDecoder(ReadOnlySpan<byte> bitstreamData)
        {
            reader = new BitstreamReader(bitstreamData);
        }

        /// <summary>
        /// Gets whether there are more bits to read.
        /// </summary>
        public readonly bool HasMoreBits => reader.HasMoreBits;

        /// <summary>
        /// Checks if there is more RBSP data beyond trailing bits (more_rbsp_data() in spec).
        /// </summary>
        public readonly bool HasMoreRbspData => reader.HasMoreRbspData();

        /// <summary>
        /// Gets the current bit position.
        /// </summary>
        public readonly int BitPosition => reader.BitPosition;

        /// <summary>Peek at the next N bits without consuming them.</summary>
        public readonly uint PeekRawBits(int count) => reader.PeekBits(count);

        /// <summary>
        /// Decodes a VLC codeword by matching peeked bits against a table of (len, bits) pairs.
        /// Returns the index of the matching entry.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeVlc(byte[] lenTable, byte[] bitsTable, int maxCodeLen)
        {
            uint peek = reader.PeekBits(maxCodeLen);

            for (int i = 0; i < lenTable.Length; i++)
            {
                int len = lenTable[i];
                if (len == 0)
                    continue;

                if ((peek >> (maxCodeLen - len)) == bitsTable[i])
                {
                    reader.Skip(len);
                    return i;
                }
            }

            throw new InvalidOperationException(
                $"Invalid VLC code in CAVLC bitstream (peek=0x{peek:X} maxLen={maxCodeLen} entries={lenTable.Length} bit={reader.BitPosition} remaining={reader.RemainingBits})");
        }

        /// <summary>
        /// Decodes a coeff_token VLC, returning the total number of coefficients
        /// and the number of trailing ones.
        /// </summary>
        /// <param name="nC">Number of non-zero coefficients context (from neighbors).</param>
        /// <param name="totalCoefficients">Output: Total number of non-zero coefficients (0-16).</param>
        /// <param name="trailingOnes">Output: Number of trailing +/-1 coefficients (0-3).</param>
        public void DecodeCoeffToken(int nC, out int totalCoefficients, out int trailingOnes)
        {
            int index;

            if (nC == -2)
            {
                // 4:2:2 chroma DC (Table 9-5, nC = -2): up to 8 coefficients
                index = DecodeVlc(Chroma422DcCoeffTokenLen, Chroma422DcCoeffTokenBits, Chroma422DcCoeffTokenMaxLen);
            }
            else if (nC < 0)
            {
                // 4:2:0 chroma DC (Table 9-5, nC = -1): up to 4 coefficients
                index = DecodeVlc(ChromaDcCoeffTokenLen, ChromaDcCoeffTokenBits, ChromaDcCoeffTokenMaxLen);
            }
            else if (nC < 2)
            {
                index = DecodeVlc(CoeffTokenLen0, CoeffTokenBits0, CoeffTokenMaxLen[0]);
            }
            else if (nC < 4)
            {
                index = DecodeVlc(CoeffTokenLen1, CoeffTokenBits1, CoeffTokenMaxLen[1]);
            }
            else if (nC < 8)
            {
                index = DecodeVlc(CoeffTokenLen2, CoeffTokenBits2, CoeffTokenMaxLen[2]);
            }
            else
            {
                index = DecodeVlc(CoeffTokenLen3, CoeffTokenBits3, CoeffTokenMaxLen[3]);
            }

            totalCoefficients = index >> 2;  // index / 4
            trailingOnes = index & 3;         // index % 4
        }

        /// <summary>
        /// Decodes the sign bits for trailing ones.
        /// </summary>
        /// <param name="signs">Output array for signs (+1 or -1), length = trailingOnes.</param>
        /// <param name="trailingOnes">Number of trailing ones (1-3).</param>
        public void DecodeTrailingOnesSigns(Span<int> signs, int trailingOnes)
        {
            for (int i = 0; i < trailingOnes; i++)
            {
                signs[i] = reader.ReadBit() == 0 ? 1 : -1;
            }
        }

        /// <summary>
        /// Decodes coefficient levels (non-trailing ones).
        /// Implements ITU-T H.264 section 9.2.2 — level parsing with suffix_length adaptation.
        /// </summary>
        public void DecodeLevels(Span<int> levels, int totalCoefficients, int trailingOnes)
        {
            int suffixLength = (totalCoefficients > 10 && trailingOnes < 3) ? 1 : 0;
            int levelCount = totalCoefficients - trailingOnes;

            for (int i = 0; i < levelCount; i++)
            {
                int level = DecodeOneLevel(suffixLength, i == 0 && trailingOnes < 3);
                levels[i] = level;

                // Update suffixLength per spec 9.2.2:
                // Step 1: ensure suffixLength >= 1 after first coefficient
                if (suffixLength == 0)
                    suffixLength = 1;
                // Step 2: increase if level exceeds threshold for current suffixLength
                if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
                    suffixLength++;
            }
        }

        /// <summary>
        /// Decodes a single coefficient level using prefix/suffix encoding.
        /// Implements H.264 Table 9-6 level_prefix and level_suffix parsing.
        /// </summary>
        private int DecodeOneLevel(int suffixLength, bool isFirstNonTrailingCoeff)
        {
            int startBit = reader.BitPosition;

            // Read level_prefix: count consecutive zero bits before the '1' terminator
            int levelPrefix = 0;
            while (reader.ReadBit() == 0)
            {
                levelPrefix++;
                if (levelPrefix > 25)
                    throw new InvalidOperationException("Invalid level prefix in CAVLC stream (>25)");
            }

            // Determine levelSuffixSize (H.264 Table 9-6)
            int levelSuffixSize;
            if (levelPrefix == 14 && suffixLength == 0)
                levelSuffixSize = 4;
            else if (levelPrefix >= 15)
                levelSuffixSize = levelPrefix - 3;
            else
                levelSuffixSize = suffixLength;

            // Read level_suffix
            int levelSuffix = levelSuffixSize > 0 ? (int)reader.ReadBits(levelSuffixSize) : 0;

            // Compute levelCode (H.264 spec 9.2.2)
            // When suffixLength == 0 and prefix >= 15, add 15 for range continuity:
            //   prefix=14/sfxLen=0 covers [14..29] (4-bit suffix), prefix=15 must start at 30
            int levelCode;
            if (levelPrefix >= 15)
            {
                levelCode = (15 << suffixLength) + levelSuffix;
                if (suffixLength == 0)
                    levelCode += 15;
                if (levelPrefix >= 16)
                    levelCode += (1 << (levelPrefix - 3)) - 4096;
            }
            else
            {
                levelCode = (levelPrefix << suffixLength) + levelSuffix;
            }

            // First non-trailing-ones coefficient gets +2 (spec: levelCode += 2 when trailing_ones < 3)
            if (isFirstNonTrailingCoeff)
                levelCode += 2;

            // Convert unsigned levelCode to signed level
            int level;
            if (levelCode % 2 == 0)
                level = (levelCode + 2) / 2;
            else
                level = -(levelCode + 1) / 2;

            return level;
        }

        /// <summary>
        /// Decodes the total number of zeros before the last non-zero coefficient.
        /// </summary>
        public int DecodeTotalZeros(int totalCoefficients, int maxCoefficients)
        {
            if (totalCoefficients >= maxCoefficients)
                return 0;

            if (maxCoefficients == 4)
                return DecodeTotalZerosChromaDc(totalCoefficients);

            if (maxCoefficients == 8)
                return DecodeTotalZerosChroma422Dc(totalCoefficients);

            return DecodeTotalZeros4x4(totalCoefficients);
        }

        /// <summary>
        /// Decodes total_zeros for 4x4 luma blocks (Table 9-7).
        /// </summary>
        private int DecodeTotalZeros4x4(int totalCoefficients)
        {
            int tableIndex = totalCoefficients - 1;
            return DecodeVlc(TotalZerosLen[tableIndex], TotalZerosBits[tableIndex],
                TotalZerosMaxLen[tableIndex]);
        }

        /// <summary>
        /// Decodes total_zeros for chroma DC blocks (Table 9-9).
        /// </summary>
        private int DecodeTotalZerosChromaDc(int totalCoefficients)
        {
            int tableIndex = totalCoefficients - 1;
            return DecodeVlc(ChromaDcTotalZerosLen[tableIndex], ChromaDcTotalZerosBits[tableIndex],
                ChromaDcTotalZerosMaxLen[tableIndex]);
        }

        /// <summary>
        /// Decodes total_zeros for 4:2:2 chroma DC blocks (Table 9-9a).
        /// </summary>
        private int DecodeTotalZerosChroma422Dc(int totalCoefficients)
        {
            int tableIndex = totalCoefficients - 1;
            return DecodeVlc(Chroma422DcTotalZerosLen[tableIndex], Chroma422DcTotalZerosBits[tableIndex],
                Chroma422DcTotalZerosMaxLen[tableIndex]);
        }

        /// <summary>
        /// Decodes run_before values for each coefficient.
        /// </summary>
        public void DecodeRunBefore(Span<int> runs, int totalCoefficients, int totalZeros)
        {
            int zerosLeft = totalZeros;

            for (int i = 0; i < totalCoefficients - 1; i++)
            {
                if (zerosLeft == 0)
                {
                    runs[i] = 0;
                }
                else
                {
                    runs[i] = DecodeRunBeforeSingle(zerosLeft);
                    zerosLeft -= runs[i];
                }
            }

            if (totalCoefficients > 0)
            {
                runs[totalCoefficients - 1] = zerosLeft;
            }
        }

        /// <summary>
        /// Decodes a single run_before value (Table 9-10).
        /// </summary>
        private int DecodeRunBeforeSingle(int zerosLeft)
        {
            int tableIndex = Math.Min(zerosLeft - 1, 6);
            return DecodeVlc(RunBeforeLen[tableIndex], RunBeforeBits[tableIndex],
                RunBeforeMaxLen[tableIndex]);
        }

        /// <summary>
        /// Decodes a complete 4x4 residual block.
        /// </summary>
        /// <param name="coefficients">Output array for 16 coefficients in scan order.</param>
        /// <param name="nC">Number of non-zero coefficients context.</param>
        /// <param name="maxCoefficients">Maximum coefficients (15 for intra16x16 luma DC, 16 otherwise).</param>
        /// <returns>Number of non-zero coefficients decoded.</returns>
        public int DecodeResidualBlock(Span<int> coefficients, int nC, int maxCoefficients = 16)
        {
            return DecodeResidualBlock(coefficients, nC, maxCoefficients, out _, out _, out _);
        }
        
        public int DecodeResidualBlock(Span<int> coefficients, int nC, int maxCoefficients, 
            out int totalZerosOut, out int trailingOnesOut, out string? levelsDebug)
        {
            coefficients.Clear();
            totalZerosOut = 0;
            trailingOnesOut = 0;
            levelsDebug = null;

            int bitStart = reader.BitPosition;
            DecodeCoeffToken(nC, out int totalCoeffs, out int trailingOnes);
            int bitAfterCT = reader.BitPosition;
            trailingOnesOut = trailingOnes;

            if (totalCoeffs == 0)
            {
                levelsDebug = $"tc=0 bitCT={bitAfterCT - bitStart}";
                return 0;
            }

            Scratch.Of16<int> levelsStorage = default;
            Span<int> levels = levelsStorage;
            Scratch.Of16<int> runsStorage = default;
            Span<int> runs = runsStorage;

            // Decode trailing ones signs (in reverse scan order)
            int levelIndex = totalCoeffs - 1;
            for (int i = 0; i < trailingOnes; i++)
            {
                int sign = reader.ReadBit() == 0 ? 1 : -1;
                levels[levelIndex--] = sign;
            }
            int bitAfterT1 = reader.BitPosition;

            // Decode remaining levels
            int remainingLevels = totalCoeffs - trailingOnes;
            Scratch.Of16<int> levelBitsStorage = default;
            Span<int> levelBits = levelBitsStorage;
            if (remainingLevels > 0)
            {
                int suffixLength = (totalCoeffs > 10 && trailingOnes < 3) ? 1 : 0;

                for (int i = remainingLevels - 1; i >= 0; i--)
                {
                    int lvlStart = reader.BitPosition;
                    bool isFirst = (i == remainingLevels - 1) && trailingOnes < 3;
                    int level = DecodeOneLevel(suffixLength, isFirst);
                    levelBits[i] = reader.BitPosition - lvlStart;

                    levels[i] = level;

                    // Update suffixLength per spec 9.2.2
                    if (suffixLength == 0)
                        suffixLength = 1;
                    if (Math.Abs(level) > (3 << (suffixLength - 1)) && suffixLength < 6)
                    {
                        suffixLength++;
                    }
                }
            }
            int bitAfterLevels = reader.BitPosition;

            // Decode total zeros
            int totalZeros = 0;
            if (totalCoeffs < maxCoefficients)
            {
                totalZeros = DecodeTotalZeros(totalCoeffs, maxCoefficients);
            }
            totalZerosOut = totalZeros;
            int bitAfterTZ = reader.BitPosition;
            
            // Build detailed debug string
            var levelBitsStr = string.Join(",", levelBits.Slice(0, remainingLevels).ToArray());
            levelsDebug = $"tc={totalCoeffs} t1={trailingOnes} tz={totalZeros} lvl=[{string.Join(",", levels.Slice(0, totalCoeffs).ToArray())}] " +
                $"bits:CT={bitAfterCT - bitStart},T1={bitAfterT1 - bitAfterCT},Lvl={bitAfterLevels - bitAfterT1}({levelBitsStr}),TZ={bitAfterTZ - bitAfterLevels}";
            // Decode run_before for each coefficient (H.264 spec 9.2.3)
            // runs[i] uses same indexing as levels[i]: i=totalCoeffs-1 is highest freq
            int zerosLeft = totalZeros;
            for (int i = totalCoeffs - 1; i > 0; i--)
            {
                if (zerosLeft == 0)
                {
                    runs[i] = 0;
                }
                else
                {
                    runs[i] = DecodeRunBeforeSingle(zerosLeft);
                    zerosLeft -= runs[i];
                }
            }
            runs[0] = zerosLeft;
            int bitAfterRB = reader.BitPosition;
            
            // Append run_before bits to debug string
            levelsDebug += $",RB={bitAfterRB - bitAfterTZ} runs=[{string.Join(",", runs.Slice(0, totalCoeffs).ToArray())}] total={bitAfterRB - bitStart}";

            // Place coefficientsin output: start at highest scan position, work toward DC
            int coeffIndex = totalCoeffs + totalZeros - 1;
            for (int i = totalCoeffs - 1; i >= 0; i--)
            {
                if (coeffIndex >= 0 && coeffIndex < coefficients.Length)
                {
                    coefficients[coeffIndex] = levels[i];
                }
                coeffIndex -= runs[i] + 1;
            }

            return totalCoeffs;
        }

        /// <summary>
        /// Reads raw bits from the underlying stream.
        /// </summary>
        public uint ReadBits(int count) => reader.ReadBits(count);

        /// <summary>
        /// Reads an unsigned exp-golomb value.
        /// </summary>
        public uint ReadExpGolombUnsigned() => reader.ReadExpGolombUnsigned();

        /// <summary>
        /// Reads a signed exp-golomb value.
        /// </summary>
        public int ReadExpGolombSigned() => reader.ReadExpGolombSigned();

        /// <summary>
        /// Reads a truncated exp-golomb value te(v) per H.264 spec 9.1.
        /// When range==0: always 0 (no bits consumed).
        /// When range==1: reads 1 bit, returns 1-bit.
        /// When range>1: falls back to ue().
        /// </summary>
        public uint ReadTruncatedExpGolomb(int range)
        {
            if (range == 0) return 0;
            if (range == 1) return 1 - reader.ReadBits(1);
            return reader.ReadExpGolombUnsigned();
        }

        /// <summary>
        /// Aligns the bitstream to the next byte boundary (required before I_PCM data).
        /// </summary>
        public void AlignToByte() => reader.AlignToByte();

        /// <summary>
        /// Gets the number of remaining bits in the bitstream.
        /// </summary>
        public readonly int RemainingBits => reader.RemainingBits;
    }
}
