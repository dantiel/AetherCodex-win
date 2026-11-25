using System;

namespace AetherCodex.Util;

/// <summary>
/// TextMate-style fuzzy path matcher. The query is a subsequence over the candidate
/// path (case-insensitive): every non-space character must appear in order anywhere
/// in the path, so "rubmain" finds "Ruby/main.rb". A space in the query matches a
/// word boundary (path separator, '_', '-', '.', or a camelCase / digit hump), so
/// "user model" finds both "user_model.rb" and "UserModel.cs". Ranking rewards
/// matches on word boundaries, consecutive runs, and the file name over directory
/// prefixes.
/// </summary>
public static class FuzzyMatcher
{
    public readonly record struct Result(double Score, IReadOnlyList<int> Matches);

    private static readonly char[] Separators = { '/', '\\', '_', '-', '.', ' ' };

    public static Result? Match(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query)) return new Result(1.0, Array.Empty<int>());
        if (string.IsNullOrEmpty(candidate)) return null;

        var matches = new List<int>(query.Length);
        double score = 0.0;
        int ci = 0;         // candidate index the next query char must satisfy
        int prev = -1;      // candidate index of the previous literal match
        int run = 0;        // length of the current consecutive match run
        int filenameStart = candidate.LastIndexOfAny(new[] { '/', '\\' }) + 1;
        bool lastWasSpace = false;

        for (int qi = 0; qi < query.Length; qi++)
        {
            char qc = char.ToLowerInvariant(query[qi]);
            if (qc == ' ')
            {
                int b = FindBoundary(candidate, lastWasSpace ? ci + 1 : ci);
                if (b < 0) return null;
                ci = b;          // zero-width: a boundary asserts position, consumes nothing
                prev = -1;
                run = 0;
                lastWasSpace = true;
                continue;
            }

            int k = FindChar(candidate, ci, qc);
            if (k < 0) return null;

            bool consecutive = prev == k - 1;
            matches.Add(k);
            score += Gain(candidate, k, filenameStart, consecutive, run, prev);
            run = consecutive ? run + 1 : 0;
            prev = k;
            ci = k + 1;
            lastWasSpace = false;
        }

        if (matches.Count == 0) return null;
        score -= matches[0] * 0.01; // prefer matches that start early in the path
        return new Result(score, matches);
    }

    private static int FindChar(string candidate, int from, char qc)
    {
        for (int i = from; i < candidate.Length; i++)
            if (char.ToLowerInvariant(candidate[i]) == qc) return i;
        return -1;
    }

    private static int FindBoundary(string candidate, int from)
    {
        for (int i = Math.Max(0, from); i < candidate.Length; i++)
        {
            char c = candidate[i];
            if (Array.IndexOf(Separators, c) >= 0) continue;
            if (i == 0) return i;
            char p = candidate[i - 1];
            if (Array.IndexOf(Separators, p) >= 0) return i;
            if (char.IsLower(p) && char.IsUpper(c)) return i;
            if (char.IsLetterOrDigit(p) && char.IsLetterOrDigit(c) && char.IsDigit(p) != char.IsDigit(c)) return i;
        }
        return -1;
    }

    private static double Gain(string candidate, int k, int filenameStart, bool consecutive, int run, int prev)
    {
        double gain = k >= filenameStart ? 1.0 : 0.55; // file name outranks directories

        if (k == 0) gain += 1.4;
        else
        {
            char p = candidate[k - 1];
            char c = candidate[k];
            if (Array.IndexOf(Separators, p) >= 0) gain += 1.2;
            else if (char.IsLower(p) && char.IsUpper(c)) gain += 0.9;
            else if (char.IsLetterOrDigit(p) && char.IsLetterOrDigit(c) && char.IsDigit(p) != char.IsDigit(c)) gain += 0.7;
        }

        if (consecutive) gain += 0.5 + 0.25 * run;
        if (prev >= 0)
        {
            int gap = k - prev - 1;
            gain -= Math.Min(gap, 10) * 0.03;
        }

        return gain;
    }
}
