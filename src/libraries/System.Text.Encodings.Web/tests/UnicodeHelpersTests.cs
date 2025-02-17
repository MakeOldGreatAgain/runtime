// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Unicode;
using Xunit;

namespace System.Text.Encodings.Web.Tests
{
    public unsafe class UnicodeHelpersTests
    {
        private const string UnicodeDataFileName = "UnicodeData.txt";

        private const int UnicodeReplacementChar = '\uFFFD';

        private static readonly UTF8Encoding _utf8EncodingThrowOnInvalidBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        [Fact]
        public void GetUtf8RepresentationForScalarValue()
        {
            for (int i = 0; i <= 0x10FFFF; i++)
            {
                if (i <= 0xFFFF && char.IsSurrogate((char)i))
                {
                    continue; // no surrogates
                }

                // Arrange
                byte[] expectedUtf8Bytes = _utf8EncodingThrowOnInvalidBytes.GetBytes(char.ConvertFromUtf32(i));

                // Act
                List<byte> actualUtf8Bytes = new List<byte>(4);
                uint asUtf8 = unchecked((uint)UnicodeHelpers.GetUtf8RepresentationForScalarValue((uint)i));
                do
                {
                    actualUtf8Bytes.Add(unchecked((byte)asUtf8));
                } while ((asUtf8 >>= 8) != 0);

                // Assert
                Assert.Equal(expectedUtf8Bytes, actualUtf8Bytes);
            }
        }

        [Fact]
        public void IsCharacterDefined()
        {
            Assert.All(ReadListOfDefinedCharacters().Select((defined, idx) => new { defined, idx }), c => Assert.Equal(c.defined, UnicodeTestHelpers.IsCharacterDefined((char)c.idx)));
        }

        private static bool[] ReadListOfDefinedCharacters()
        {
            HashSet<string> allowedCategories = new HashSet<string>();

            // Letters
            allowedCategories.Add("Lu");
            allowedCategories.Add("Ll");
            allowedCategories.Add("Lt");
            allowedCategories.Add("Lm");
            allowedCategories.Add("Lo");

            // Marks
            allowedCategories.Add("Mn");
            allowedCategories.Add("Mc");
            allowedCategories.Add("Me");

            // Numbers
            allowedCategories.Add("Nd");
            allowedCategories.Add("Nl");
            allowedCategories.Add("No");

            // Punctuation
            allowedCategories.Add("Pc");
            allowedCategories.Add("Pd");
            allowedCategories.Add("Ps");
            allowedCategories.Add("Pe");
            allowedCategories.Add("Pi");
            allowedCategories.Add("Pf");
            allowedCategories.Add("Po");

            // Symbols
            allowedCategories.Add("Sm");
            allowedCategories.Add("Sc");
            allowedCategories.Add("Sk");
            allowedCategories.Add("So");

            // Separators
            // With the exception of U+0020 SPACE, these aren't allowed

            // Other
            // We only allow one category of 'other' characters
            allowedCategories.Add("Cf");

            HashSet<string> seenCategories = new HashSet<string>();

            bool[] retVal = new bool[0x10000];
            string[] allLines = new StreamReader(typeof(UnicodeHelpersTests).GetTypeInfo().Assembly.GetManifestResourceStream(UnicodeDataFileName)).ReadAllLines();

            uint startSpanCodepoint = 0;
            foreach (string line in allLines)
            {
                string[] splitLine = line.Split(';');
                uint codePoint = uint.Parse(splitLine[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                if (codePoint >= retVal.Length)
                {
                    continue; // don't care about supplementary chars
                }

                if (codePoint == (uint)' ')
                {
                    retVal[codePoint] = true; // we allow U+0020 SPACE as our only valid Zs (whitespace) char
                }
                else if (codePoint == 0xFEFF)
                {
                    retVal[codePoint] = false; // we explicitly forbid U+FEFF ZERO WIDTH NO-BREAK SPACE because it's also the byte order mark (BOM)
                }
                else
                {
                    string category = splitLine[2];

                    if (allowedCategories.Contains(category))
                    {
                        retVal[codePoint] = true; // chars in this category are allowable
                        seenCategories.Add(category);

                        if (splitLine[1].EndsWith("First>"))
                        {
                            startSpanCodepoint = codePoint;
                        }
                        else if (splitLine[1].EndsWith("Last>"))
                        {
                            for (uint spanCounter = startSpanCodepoint; spanCounter < codePoint; spanCounter++)
                            {
                                retVal[spanCounter] = true; // chars in this category are allowable
                            }
                        }

                    }
                }
            }

            // Finally, we need to make sure we've seen every category which contains
            // allowed characters. This provides extra defense against having a typo
            // in the list of categories.
            Assert.Equal(allowedCategories.OrderBy(c => c), seenCategories.OrderBy(c => c));

            return retVal;
        }
    }
}
