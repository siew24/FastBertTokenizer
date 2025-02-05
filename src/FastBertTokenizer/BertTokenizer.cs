// Copyright (c) Georg Jung. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using NStack;

namespace FastBertTokenizer
{
    /// <summary>
    /// How attention_mask, input_ids and token_type_ids are created: https://huggingface.co/transformers/v3.2.0/glossary.html.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Have private overload close to public one.")]
    public partial class BertTokenizer
    {
        private Dictionary<string, long>? _prefixes;
        private Dictionary<string, long>? _suffixes;
        private (int Id, string Token) _unk = default!;
        private (int Id, string Token) _cls = default!;
        private (int Id, string Token) _sep = default!;
        private (int Id, string Token) _pad = default!;
        private bool _lowercaseInput;
        private NormalizationForm _normalization;

        /// <summary>
        /// Encode the given input string to token ids per the loaded vocabulary. Write the results to the
        /// given memory areas. When encoding multiple inputs successivly it is more efficient to reuse the
        /// memory for the results than allocating new memory and returing new arrays.
        /// </summary>
        /// <param name="input">The input to encode.</param>
        /// <param name="inputIds">
        /// The resulting token ids/input_ids will be written here. The token_ids
        /// list will be truncated (and correctly ended with a [SEP] token) if the
        /// input translates to more tokens than the list can hold.
        /// </param>
        /// <param name="attentionMask">
        /// The attention mask for the given input will be written here. At the positions
        /// of padding tokens in <paramref name="inputIds"/> attention mask will be 0.
        /// All other (relevant/interesting) positions will have a value of 1.
        /// </param>
        /// <param name="tokenTypeIds">
        /// Will be filled with 0s. Use the overload without this parameter for optimized speed.
        /// Some models which can take multiple sequences as input might need this but this is
        /// currently not supported by FastBertTokenizer.
        /// </param>
        /// <param name="padTo">
        /// Fill the given destination memory areas with padding tokens up to this length.
        /// </param>
        /// <returns>The number of token ids produced.</returns>
        public int Tokenize(string input, Memory<long> inputIds, Span<long> attentionMask, Span<long> tokenTypeIds, int? padTo = null)
        {
            var inputIdCnt = Tokenize(input, inputIds, attentionMask, padTo);
            tokenTypeIds.Slice(0, inputIdCnt).Fill(0);

            return inputIdCnt;
        }

        /// <inheritdoc cref="Tokenize(string, Memory{long}, Span{long}, Span{long}, int?)"/>
        public int Tokenize(string input, Memory<long> inputIds, Span<long> attentionMask, int? padTo = null)
        {
            var (inputIdCnt, nonPaddedCnt) = Tokenize(input, inputIds, padTo);
            attentionMask.Slice(0, nonPaddedCnt).Fill(1);
            attentionMask.Slice(nonPaddedCnt, inputIdCnt - nonPaddedCnt).Fill(0);
            return inputIdCnt;
        }

        /// <summary>
        /// Encode the given input string to token ids per the loaded vocabulary. This overload allocated new memory to write its results to.
        /// Thus, it is less efficient than the overloads that take memory areas to write to. Consider using those if you need to encode multiple
        /// inputs successivly.
        /// </summary>
        /// <param name="input">The input to encode.</param>
        /// <param name="maximumTokens">The maximum number of token ids to encode. Most bert models support inputs of up to 512 tokens.</param>
        /// <param name="padTo">Create an input_ids array of at least this length and fill possible unused positions at the end with the padding token id.</param>
        /// <returns>input_ids, attention_mask and token_type_ids that might be passed to typical BERT models.</returns>
        public (Memory<long> InputIds, Memory<long> AttentionMask, Memory<long> TokenTypeIds) Tokenize(string input, int maximumTokens = 512, int? padTo = null)
        {
            var inputIds = new long[maximumTokens];
            var (inputIdCnt, nonPaddedCnt) = Tokenize(input, inputIds, padTo);
            var attM = new long[inputIdCnt];
            var tokTypI = new long[inputIdCnt];
            Array.Fill(attM, 1, 0, nonPaddedCnt);
            Array.Fill(attM, 0, nonPaddedCnt, inputIdCnt - nonPaddedCnt);
            Array.Fill(tokTypI, 0);
            return (inputIds.AsMemory(0, inputIdCnt), attM, tokTypI);
        }

        private (int Length, int NonPadding) Tokenize(string input, Memory<long> inputIds, int? padTo = null)
        {
            _ = _prefixes ?? throw new InvalidOperationException("Vocabulary not loaded.");
            _ = _suffixes ?? throw new InvalidOperationException("Vocabulary not loaded.");

            var inputIdsSpan = inputIds.Span;
            var maximumTokens = inputIds.Length;
            var inputIdCnt = 1;
            inputIdsSpan[0] = _cls.Id;
            PreTokenizer.PreTokenize(input, OnWordToken, _lowercaseInput, _normalization);

            bool OnWordToken(ReadOnlySpan<char> word)
            {
                var span = inputIds.Span;
                var added = TokenizeSubword(word, span.Slice(inputIdCnt, span.Length - inputIdCnt));
                if (inputIdCnt + added + 1 > maximumTokens)
                {
                    // HuggingFace tokenizer does add partial words.
                    inputIdCnt = maximumTokens - 1; // leave one out for the final [SEP] token
                    return false;
                }

                inputIdCnt += added;
                return inputIdCnt + 1 < maximumTokens;
            }

            inputIds.Span[inputIdCnt] = _sep.Id;
            inputIdCnt++;
            var nonPaddedCnt = inputIdCnt;

            if (padTo is int padLen && padLen > inputIdCnt)
            {
                inputIdsSpan.Slice(inputIdCnt, padLen - inputIdCnt).Fill(_pad.Id);
                inputIdCnt = padLen;
            }

            return (inputIdCnt, nonPaddedCnt);
        }

        /// <summary>
        /// Inspired by https://github.com/huggingface/transformers/blob/7db1ad63d9a9a8f705e13d68f90269df78a16df5/src/transformers/tokenization_utils.py#L280.
        /// We don't filter \t, \r, \n because splitting by whitespace was already done.
        /// As per https://en.wikipedia.org/wiki/Unicode_character_property#General_Category, Control, Format, Surrogate, PrivateUse and OtherNotAssigned
        /// are all categories starting with "C".
        /// </summary>
        /// <param name="text">Text to remove special unicode chars from.</param>
        /// <param name="cleaned">Contains the cleaned text.</param>
        /// <returns>True if characters were removed.</returns>
        private static bool RemoveControlAndReplacement(ReadOnlySpan<char> text, out ReadOnlySpan<char> cleaned)
        {
            bool NeedsRemoval(ReadOnlySpan<char> text)
            {
                foreach (Rune r in ustring.Make(text.ToArray()))
                {
                    if (r.Value == 0xFFFD)
                    {
                        return true;
                    }

                    if (Unicode.IsRuneInRanges(r, Unicode.Category.Cc))
                    {
                        return true;
                    }

                    if (Unicode.IsRuneInRanges(r, Unicode.Category.Cf))
                    {
                        return true;
                    }

                    if (Unicode.IsRuneInRanges(r, Unicode.Category.Cs))
                    {
                        return true;
                    }

                    if (Unicode.IsRuneInRanges(r, Unicode.Category.Co))
                    {
                        return true;
                    }

                    // OtherNotAssigned Category not done
                    break;
                }

                return false;
            }

            if (!NeedsRemoval(text))
            {
                cleaned = text;
                return false;
            }

            int i = 0;
            var charArray = new char[text.Length];

            foreach (Rune r in ustring.Make(text.ToArray()))
            {
                if (r.Value == 0xFFFD)
                {
                    continue;
                }

                if (Unicode.IsRuneInRanges(r, Unicode.Category.Cc))
                {
                    continue;
                }

                if (Unicode.IsRuneInRanges(r, Unicode.Category.Cf))
                {
                    continue;
                }

                if (Unicode.IsRuneInRanges(r, Unicode.Category.Cs))
                {
                    continue;
                }

                if (Unicode.IsRuneInRanges(r, Unicode.Category.Co))
                {
                    continue;
                }

                // OtherNotAssigned Category not done
                byte[] bytes = new byte[charArray.AsSpan().Slice(i).Length];

                Rune.EncodeRune(r, bytes);

                Encoding.Unicode.GetChars(bytes).CopyTo(charArray, i++);

                if (r.Value >= 0 && r.Value <= 0xFFFF)
                {
                    i++;
                }
            }

            cleaned = charArray.AsSpan().Slice(0, i);
            return true;
        }

        /// <summary>
        /// Source: https://stackoverflow.com/a/67190157/1200847.
        /// Similar to what HuggingFace tokenizer does in _run_strip_accents:
        /// https://github.com/huggingface/transformers/blob/7db1ad63d9a9a8f705e13d68f90269df78a16df5/src/transformers/models/bert/tokenization_bert.py#L449.
        /// </summary>
        /// <param name="text">String to remove diacritics from.</param>
        /// <param name="targetNf">The returned value will be unicode normalized in the form.</param>
        /// <returns>String without diacritics.</returns>
        private static string RemoveDiacritics(string text, NormalizationForm targetNf)
        {
            bool NeedsRemoval(ReadOnlySpan<char> formD)
            {
                foreach (char c in formD)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    {
                        return true;
                    }
                }

                return false;
            }

            ReadOnlySpan<char> normalizedString = text.Normalize(NormalizationForm.FormD);

            if (!NeedsRemoval(normalizedString))
            {
                return text;
            }

            int i = 0;
            Span<char> span = normalizedString.Length < 1000
                ? stackalloc char[normalizedString.Length]
                : new char[normalizedString.Length];

            foreach (char c in normalizedString)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);

                // ToLowerInvariant performed by pre-tokenizer does not lower all chars with diacrits.
                if (cat == UnicodeCategory.UppercaseLetter || cat == UnicodeCategory.TitlecaseLetter)
                {
                    span[i++] = char.ToLowerInvariant(c);
                }
                else if (cat != UnicodeCategory.NonSpacingMark)
                {
                    span[i++] = c;
                }
            }

            return new string(span.Slice(0, i)).Normalize(targetNf);
        }

        private int TokenizeSubword(ReadOnlySpan<char> word, Span<long> tokenIdSink)
        {
            int OnUnknown(ReadOnlySpan<char> word, Span<long> tokenIdSink)
            {
                if (RemoveControlAndReplacement(word, out var withoutControl))
                {
                    if (withoutControl.Length == 0)
                    {
                        return 0;
                    }

                    return TokenizeSubword(withoutControl, tokenIdSink);
                }

                // Normalize and IsNormalized for ReadOnlySpan<char> is not yet implemented:
                // https://github.com/dotnet/runtime/issues/87757
                // RemoveDiacritics ends up in form _normalization too.
                // If we have a vocab that includes diacritics we might sill want to normalize first
                // and try again before removing diacritics.
                var wordStr = word.ToString();
                if (!wordStr.IsNormalized(_normalization))
                {
                    return TokenizeSubword(wordStr.Normalize(_normalization), tokenIdSink);
                }

                var withoutDiacrit = RemoveDiacritics(wordStr, _normalization);
                if (!MemoryExtensions.Equals(withoutDiacrit, word, StringComparison.Ordinal))
                {
                    return TokenizeSubword(withoutDiacrit.AsSpan(), tokenIdSink);
                }

                tokenIdSink[0] = _unk.Id;
                return 1;
            }

            // No null checks for _prefixes and _suffixes because this is a private method.
            var prefix = word;
            var cnt = 0;
            long id = -1;

            // ToDo: Remove string allocation; related: https://github.com/dotnet/runtime/issues/27229
            while (prefix.Length > 0)
            {
                if (_prefixes!.TryGetValue(new string(prefix), out var outId))
                {
                    id = outId;
                    break;
                }

                prefix = prefix.Slice(0, prefix.Length - 1);
            }

            if (id == -1)
            {
                return OnUnknown(word, tokenIdSink);
            }

            tokenIdSink[0] = id;
            cnt++;

            var remaining = word.Slice(prefix.Length);
            while (remaining.Length > 0 && cnt < tokenIdSink.Length)
            {
                var suffix = remaining;
                id = -1;

                // ToDo: Remove string allocation; related: https://github.com/dotnet/runtime/issues/27229
                while (suffix.Length > 0)
                {
                    if (_suffixes!.TryGetValue(new string(suffix), out var outId))
                    {
                        id = outId;
                        break;
                    }

                    suffix = suffix.Slice(0, suffix.Length - 1);
                }

                if (id == -1)
                {
                    return OnUnknown(word, tokenIdSink);
                }

                tokenIdSink[cnt] = id;
                cnt++;
                remaining = remaining.Slice(suffix.Length);
            }

            return cnt;
        }
    }
}
