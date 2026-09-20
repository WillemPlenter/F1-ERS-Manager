using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

internal sealed class F1M24PlayerTeamInfo
{
    internal readonly int TeamId;
    internal readonly int DriverOneStaffId;
    internal readonly int DriverTwoStaffId;
    internal readonly string Source;
    internal readonly DateTime LastWriteUtc;

    internal F1M24PlayerTeamInfo(int teamId, int driverOneStaffId,
        int driverTwoStaffId, string source, DateTime lastWriteUtc)
    {
        TeamId = teamId;
        DriverOneStaffId = driverOneStaffId;
        DriverTwoStaffId = driverTwoStaffId;
        Source = source ?? String.Empty;
        LastWriteUtc = lastWriteUtc;
    }
}

// Reads only the player's TeamID from F1M24's normal autosave. The save and
// embedded SQLite database stay in memory; this class never changes game files.
internal static class F1M24SaveTeamReader
{
    const int MaximumSaveBytes = 64 * 1024 * 1024;
    const int MaximumDatabaseBytes = 128 * 1024 * 1024;
    const int MaximumCandidateFiles = 64;

    static readonly byte[] DatabaseMarker = new byte[] {
        0x00, 0x05, 0x00, 0x00, 0x00, 0x4E, 0x6F, 0x6E, 0x65,
        0x00, 0x05, 0x00, 0x00, 0x00, 0x4E, 0x6F, 0x6E, 0x65
    };

    internal static bool TryReadCurrentTeamId(out int teamId, out string source,
        out string error)
    {
        teamId = 0;
        source = String.Empty;
        error = String.Empty;
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "F1Manager24", "Saved", "SaveGames");
            if (!Directory.Exists(directory))
            {
                error = "the F1M24 save folder does not exist yet";
                return false;
            }

            string autosave = Path.Combine(directory, "autosave.sav");
            string selected = File.Exists(autosave) ? autosave : NewestSave(directory);
            if (String.IsNullOrEmpty(selected))
            {
                error = "no F1M24 save found";
                return false;
            }

            byte[] save;
            DateTime ignoredLastWrite;
            if (!TryReadStableFile(selected, out save, out ignoredLastWrite,
                out error)) return false;
            if (!TryReadTeamIdFromSave(save, out teamId))
            {
                error = "TeamID could not be read safely from " + Path.GetFileName(selected);
                return false;
            }
            source = selected;
            return true;
        }
        catch (Exception exception)
        {
            if (exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidDataException || exception is ArgumentException)
            {
                error = exception.Message;
                return false;
            }
            throw;
        }
    }

    internal static bool TryReadCandidateTeams(
        out List<F1M24PlayerTeamInfo> candidates, out string error)
    {
        candidates = new List<F1M24PlayerTeamInfo>();
        error = String.Empty;
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "F1Manager24", "Saved", "SaveGames");
            if (!Directory.Exists(directory))
            {
                error = "the F1M24 save folder does not exist yet";
                return false;
            }

            List<string> paths = CandidateSavePaths(directory);
            if (paths.Count == 0)
            {
                error = "no F1M24 save found";
                return false;
            }

            var profiles = new HashSet<string>(StringComparer.Ordinal);
            string firstReadError = String.Empty;
            for (int i = 0; i < paths.Count; i++)
            {
                byte[] save;
                DateTime lastWriteUtc;
                string readError;
                if (!TryReadStableFile(paths[i], out save, out lastWriteUtc,
                    out readError))
                {
                    if (firstReadError.Length == 0)
                        firstReadError = Path.GetFileName(paths[i]) + ": " + readError;
                    continue;
                }

                F1M24PlayerTeamInfo profile;
                if (!TryReadTeamInfoFromSave(save, paths[i], lastWriteUtc,
                    out profile)) continue;
                string key = profile.TeamId.ToString() + "|" +
                    profile.DriverOneStaffId.ToString() + "|" +
                    profile.DriverTwoStaffId.ToString();
                if (profiles.Add(key)) candidates.Add(profile);
            }

            if (candidates.Count != 0) return true;
            error = firstReadError.Length == 0
                ? "no save contained an unambiguous current player team"
                : "no stably readable player team found (" + firstReadError + ")";
            return false;
        }
        catch (Exception exception)
        {
            if (exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidDataException || exception is ArgumentException)
            {
                error = exception.Message;
                return false;
            }
            throw;
        }
    }

    // Autosave is the game's current-session source. Falling back to the
    // newest valid manual save is allowed only when no autosave exists.
    internal static bool TryReadCurrentTeamInfo(
        out F1M24PlayerTeamInfo profile, out string error)
    {
        profile = null;
        error = String.Empty;
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "F1Manager24", "Saved", "SaveGames");
            if (!Directory.Exists(directory))
            {
                error = "the F1M24 save folder does not exist yet";
                return false;
            }

            string autosave = Path.Combine(directory, "autosave.sav");
            if (File.Exists(autosave))
                return TryReadTeamInfoFile(autosave, out profile, out error);

            List<string> paths = CandidateSavePaths(directory);
            for (int i = 0; i < paths.Count; i++)
            {
                string ignored;
                if (TryReadTeamInfoFile(paths[i], out profile, out ignored))
                    return true;
            }
            error = paths.Count == 0
                ? "no F1M24 save found"
                : "no valid current F1M24 save found";
            return false;
        }
        catch (Exception exception)
        {
            if (exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidDataException || exception is ArgumentException)
            {
                error = exception.Message;
                return false;
            }
            throw;
        }
    }

    static bool TryReadTeamInfoFile(string path,
        out F1M24PlayerTeamInfo profile, out string error)
    {
        profile = null;
        byte[] save;
        DateTime lastWriteUtc;
        if (!TryReadStableFile(path, out save, out lastWriteUtc, out error))
            return false;
        if (!TryReadTeamInfoFromSave(save, path, lastWriteUtc, out profile))
        {
            error = "team and current drivers could not be read safely from " +
                Path.GetFileName(path);
            return false;
        }
        return true;
    }

    static List<string> CandidateSavePaths(string directory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string autosave = Path.Combine(directory, "autosave.sav");
        if (File.Exists(autosave))
        {
            result.Add(autosave);
            seen.Add(Path.GetFullPath(autosave));
        }

        string[] files = Directory.GetFiles(directory, "*.sav");
        Array.Sort(files, delegate(string left, string right)
        {
            DateTime leftWrite;
            DateTime rightWrite;
            try { leftWrite = File.GetLastWriteTimeUtc(left); }
            catch { leftWrite = DateTime.MinValue; }
            try { rightWrite = File.GetLastWriteTimeUtc(right); }
            catch { rightWrite = DateTime.MinValue; }
            int byDate = rightWrite.CompareTo(leftWrite);
            return byDate != 0 ? byDate :
                StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });
        for (int i = 0; i < files.Length &&
            result.Count < MaximumCandidateFiles; i++)
        {
            string full = Path.GetFullPath(files[i]);
            if (seen.Add(full)) result.Add(files[i]);
        }
        return result;
    }

    static string NewestSave(string directory)
    {
        string newest = null;
        DateTime newestWrite = DateTime.MinValue;
        foreach (string path in Directory.GetFiles(directory, "*.sav"))
        {
            DateTime write;
            try { write = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (newest == null || write > newestWrite)
            {
                newest = path;
                newestWrite = write;
            }
        }
        return newest;
    }

    static bool TryReadStableFile(string path, out byte[] data,
        out DateTime lastWriteUtc, out string error)
    {
        data = null;
        lastWriteUtc = DateTime.MinValue;
        error = String.Empty;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var before = new FileInfo(path);
                before.Refresh();
                long expectedLength = before.Length;
                DateTime expectedWrite = before.LastWriteTimeUtc;
                if (expectedLength <= 0 || expectedLength > MaximumSaveBytes ||
                    expectedLength > Int32.MaxValue)
                {
                    error = "save file has an invalid size";
                    return false;
                }

                byte[] result;
                long openedLength;
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    openedLength = stream.Length;
                    if (openedLength != expectedLength) continue;
                    result = new byte[(int)openedLength];
                    int done = 0;
                    while (done < result.Length)
                    {
                        int read = stream.Read(result, done, result.Length - done);
                        if (read <= 0) break;
                        done += read;
                    }
                    if (done != result.Length || stream.Length != openedLength)
                        continue;
                }

                var after = new FileInfo(path);
                after.Refresh();
                if (after.Length != expectedLength ||
                    after.LastWriteTimeUtc != expectedWrite) continue;
                data = result;
                lastWriteUtc = expectedWrite;
                return true;
            }
            error = "save file changed while it was being read";
            return false;
        }
        catch (IOException exception) { error = exception.Message; return false; }
        catch (UnauthorizedAccessException exception)
        { error = exception.Message; return false; }
        catch (ArgumentException exception) { error = exception.Message; return false; }
    }

    internal static bool TryReadTeamIdFromSave(byte[] save, out int teamId)
    {
        teamId = 0;
        byte[] database;
        if (!TryExtractMainDatabase(save, out database)) return false;
        return TryReadTeamIdFromDatabase(database, out teamId);
    }

    internal static bool TryReadTeamInfoFromSave(byte[] save, string source,
        DateTime lastWriteUtc, out F1M24PlayerTeamInfo profile)
    {
        profile = null;
        byte[] database;
        if (!TryExtractMainDatabase(save, out database)) return false;
        int teamId;
        int driverOne;
        int driverTwo;
        if (!TryReadPlayerTeamInfoFromDatabase(database, out teamId,
            out driverOne, out driverTwo)) return false;
        profile = new F1M24PlayerTeamInfo(teamId, driverOne, driverTwo,
            source, lastWriteUtc);
        return true;
    }

    static bool TryExtractMainDatabase(byte[] save, out byte[] database)
    {
        database = null;
        if (save == null || save.Length < DatabaseMarker.Length + 25) return false;
        int marker = LastIndexOf(save, DatabaseMarker);
        if (marker < 0) return false;
        int header = marker + DatabaseMarker.Length + 5;
        if (header < 0 || header + 16 > save.Length) return false;

        int compressedSize = BitConverter.ToInt32(save, header);
        int mainSize = BitConverter.ToInt32(save, header + 4);
        int backupOneSize = BitConverter.ToInt32(save, header + 8);
        int backupTwoSize = BitConverter.ToInt32(save, header + 12);
        int compressedAt = header + 16;
        long totalSize = (long)mainSize + backupOneSize + backupTwoSize;
        if (compressedSize < 6 || compressedSize > MaximumSaveBytes ||
            mainSize <= 0 || backupOneSize < 0 || backupTwoSize < 0 ||
            totalSize < mainSize || totalSize > MaximumDatabaseBytes ||
            compressedAt < 0 || compressedAt > save.Length ||
            compressedSize != save.Length - compressedAt) return false;

        return TryInflateMainDatabase(save, compressedAt, compressedSize,
            mainSize, backupOneSize, backupTwoSize, out database);
    }

    static bool TryInflateMainDatabase(byte[] save, int start,
        int compressedSize, int mainSize, int backupOneSize,
        int backupTwoSize, out byte[] database)
    {
        database = null;
        if (start < 0 || compressedSize < 6 ||
            start + (long)compressedSize > save.Length) return false;
        byte cmf = save[start];
        byte flg = save[start + 1];
        if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 ||
            (((int)cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            return false;
        int deflateStart = start + 2;
        int deflateLength = compressedSize - 6;
        long expectedSize = (long)mainSize + backupOneSize + backupTwoSize;
        uint expectedAdler = ((uint)save[start + compressedSize - 4] << 24) |
            ((uint)save[start + compressedSize - 3] << 16) |
            ((uint)save[start + compressedSize - 2] << 8) |
            save[start + compressedSize - 1];
        try
        {
            byte[] result = new byte[mainSize];
            byte[] buffer = new byte[8192];
            long inflated = 0;
            uint adlerA = 1;
            uint adlerB = 0;
            using (MemoryStream input = new MemoryStream(save, deflateStart,
                deflateLength, false))
            using (DeflateStream deflate = new DeflateStream(input,
                CompressionMode.Decompress, false))
            {
                while (inflated < expectedSize)
                {
                    int wanted = (int)Math.Min((long)buffer.Length,
                        expectedSize - inflated);
                    int read = deflate.Read(buffer, 0, wanted);
                    if (read <= 0) return false;
                    if (inflated < mainSize)
                    {
                        int copy = (int)Math.Min((long)read,
                            mainSize - inflated);
                        Buffer.BlockCopy(buffer, 0, result, (int)inflated, copy);
                    }
                    for (int i = 0; i < read; i++)
                    {
                        adlerA += buffer[i];
                        if (adlerA >= 65521U) adlerA -= 65521U;
                        adlerB += adlerA;
                        if (adlerB >= 65521U) adlerB -= 65521U;
                    }
                    inflated += read;
                }
                if (deflate.ReadByte() != -1) return false;
            }
            uint actualAdler = (adlerB << 16) | adlerA;
            if (actualAdler != expectedAdler) return false;
            database = result;
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (int at = haystack.Length - needle.Length; at >= 0; at--)
        {
            bool equal = true;
            for (int i = 0; i < needle.Length; i++)
                if (haystack[at + i] != needle[i]) { equal = false; break; }
            if (equal) return at;
        }
        return -1;
    }

    internal static bool TryReadTeamIdFromDatabase(byte[] database, out int teamId)
    {
        teamId = 0;
        try
        {
            var sqlite = new SqliteReader(database);
            return TryReadPlayerTeamId(sqlite, out teamId);
        }
        catch (InvalidDataException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (OverflowException) { return false; }
    }

    static bool TryReadPlayerTeamInfoFromDatabase(byte[] database,
        out int teamId, out int driverOneStaffId, out int driverTwoStaffId)
    {
        teamId = 0;
        driverOneStaffId = 0;
        driverTwoStaffId = 0;
        try
        {
            var sqlite = new SqliteReader(database);
            if (!TryReadPlayerTeamId(sqlite, out teamId)) return false;

            int driverRoot;
            string driverSql;
            if (!sqlite.TryFindTable("Staff_DriverData", out driverRoot,
                out driverSql)) return false;
            int driverStaffColumn = FindColumn(driverSql, "StaffID");
            if (driverStaffColumn < 0 ||
                !IsIntegerPrimaryKeyColumn(driverSql, "StaffID")) return false;
            List<SqliteRow> driverRows;
            if (!sqlite.TryReadTableRows(driverRoot, out driverRows)) return false;
            var driverIds = new HashSet<int>();
            for (int i = 0; i < driverRows.Count; i++)
            {
                int staffId;
                if (!TryReadIntegerPrimaryKey(driverRows[i], driverStaffColumn,
                    out staffId) || staffId <= 0 || !driverIds.Add(staffId))
                    return false;
            }
            if (driverIds.Count == 0) return false;

            int contractsRoot;
            string contractsSql;
            if (!sqlite.TryFindTable("Staff_Contracts", out contractsRoot,
                out contractsSql)) return false;
            int staffColumn = FindColumn(contractsSql, "StaffID");
            int typeColumn = FindColumn(contractsSql, "ContractType");
            int teamColumn = FindColumn(contractsSql, "TeamID");
            int positionColumn = FindColumn(contractsSql, "PosInTeam");
            if (staffColumn < 0 || typeColumn < 0 || teamColumn < 0 ||
                positionColumn < 0) return false;

            List<SqliteRow> contractRows;
            if (!sqlite.TryReadTableRows(contractsRoot, out contractRows))
                return false;
            int? first = null;
            int? second = null;
            for (int i = 0; i < contractRows.Count; i++)
            {
                object[] values = contractRows[i].Values;
                int maximum = Math.Max(Math.Max(staffColumn, typeColumn),
                    Math.Max(teamColumn, positionColumn));
                if (maximum >= values.Length) return false;
                long staff;
                long contractType;
                long contractTeam;
                long position;
                if (!TryInteger(values[staffColumn], out staff) ||
                    !TryInteger(values[typeColumn], out contractType) ||
                    !TryInteger(values[teamColumn], out contractTeam) ||
                    !TryInteger(values[positionColumn], out position))
                    return false;
                if (contractType != 0 || contractTeam != teamId ||
                    (position != 1 && position != 2)) continue;
                if (staff <= 0 || staff > Int32.MaxValue ||
                    !driverIds.Contains((int)staff)) continue;
                int id = (int)staff;
                if (position == 1)
                {
                    if (first.HasValue && first.Value != id) return false;
                    first = id;
                }
                else
                {
                    if (second.HasValue && second.Value != id) return false;
                    second = id;
                }
            }
            if (!first.HasValue || !second.HasValue ||
                first.Value == second.Value) return false;
            driverOneStaffId = first.Value;
            driverTwoStaffId = second.Value;
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (OverflowException) { return false; }
    }

    static bool TryReadPlayerTeamId(SqliteReader sqlite, out int teamId)
    {
        teamId = 0;
        int rootPage;
        string createSql;
        if (!sqlite.TryFindTable("Player", out rootPage, out createSql)) return false;
        int column = FindColumn(createSql, "TeamID");
        if (column < 0) return false;

        List<object[]> rows;
        if (!sqlite.TryReadTable(rootPage, out rows)) return false;
        int? found = null;
        for (int i = 0; i < rows.Count; i++)
        {
            if (column >= rows[i].Length) return false;
            long value;
            if (!TryInteger(rows[i][column], out value) ||
                value <= 0 || value > 255) return false;
            if (found.HasValue && found.Value != (int)value) return false;
            found = (int)value;
        }
        if (!found.HasValue) return false;
        teamId = found.Value;
        return true;
    }

    static bool TryReadIntegerPrimaryKey(SqliteRow row, int column,
        out int value)
    {
        value = 0;
        if (row == null || column < 0 || column >= row.Values.Length)
            return false;
        long stored;
        if (TryInteger(row.Values[column], out stored))
        {
            if (stored <= 0 || stored > Int32.MaxValue) return false;
            value = (int)stored;
            return true;
        }
        if (row.Values[column] != null || row.RowId == 0 ||
            row.RowId > Int32.MaxValue) return false;
        value = (int)row.RowId;
        return true;
    }

    static bool TryInteger(object value, out long number)
    {
        if (value is long) { number = (long)value; return true; }
        if (value is int) { number = (int)value; return true; }
        number = 0;
        return false;
    }

    static int FindColumn(string createSql, string wanted)
    {
        if (String.IsNullOrEmpty(createSql)) return -1;
        int open = createSql.IndexOf('(');
        int close = createSql.LastIndexOf(')');
        if (open < 0 || close <= open) return -1;
        List<string> definitions = SplitDefinitions(
            createSql.Substring(open + 1, close - open - 1));
        int column = 0;
        for (int i = 0; i < definitions.Count; i++)
        {
            string name = FirstIdentifier(definitions[i]);
            if (name.Length == 0 || IsTableConstraint(name)) continue;
            if (String.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return column;
            column++;
        }
        return -1;
    }

    static bool IsIntegerPrimaryKeyColumn(string createSql, string wanted)
    {
        if (String.IsNullOrEmpty(createSql)) return false;
        int open = createSql.IndexOf('(');
        int close = createSql.LastIndexOf(')');
        if (open < 0 || close <= open) return false;
        List<string> definitions = SplitDefinitions(
            createSql.Substring(open + 1, close - open - 1));
        for (int i = 0; i < definitions.Count; i++)
        {
            if (!String.Equals(FirstIdentifier(definitions[i]), wanted,
                StringComparison.OrdinalIgnoreCase)) continue;
            string upper = definitions[i].ToUpperInvariant();
            return upper.IndexOf("INTEGER", StringComparison.Ordinal) >= 0 &&
                upper.IndexOf("PRIMARY", StringComparison.Ordinal) >= 0 &&
                upper.IndexOf("KEY", StringComparison.Ordinal) >= 0;
        }
        return false;
    }

    static List<string> SplitDefinitions(string value)
    {
        var result = new List<string>();
        int start = 0, depth = 0;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                if ((quote == '[' && c == ']') || (quote != '[' && c == quote))
                    quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"' || c == '`' || c == '[') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(value.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(value.Substring(start));
        return result;
    }

    static string FirstIdentifier(string definition)
    {
        string value = definition.Trim();
        if (value.Length == 0) return String.Empty;
        char first = value[0];
        if (first == '"' || first == '`' || first == '[')
        {
            char close = first == '[' ? ']' : first;
            int end = value.IndexOf(close, 1);
            return end > 1 ? value.Substring(1, end - 1) : String.Empty;
        }
        int length = 0;
        while (length < value.Length && !Char.IsWhiteSpace(value[length]) &&
            value[length] != '(') length++;
        return value.Substring(0, length);
    }

    static bool IsTableConstraint(string value)
    {
        return value.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("CHECK", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase);
    }

    sealed class SqliteRow
    {
        internal readonly ulong RowId;
        internal readonly object[] Values;

        internal SqliteRow(ulong rowId, object[] values)
        {
            RowId = rowId;
            Values = values;
        }
    }

    sealed class SqliteReader
    {
        readonly byte[] data;
        readonly int pageSize;
        readonly int usableSize;
        readonly Encoding textEncoding;

        internal SqliteReader(byte[] database)
        {
            data = database;
            byte[] signature = Encoding.ASCII.GetBytes("SQLite format 3\0");
            if (data == null || data.Length < 512) throw new InvalidDataException();
            for (int i = 0; i < signature.Length; i++)
                if (data[i] != signature[i]) throw new InvalidDataException();
            int encodedSize = ReadUInt16(16);
            pageSize = encodedSize == 1 ? 65536 : encodedSize;
            if (pageSize < 512 || pageSize > 65536 ||
                (pageSize & (pageSize - 1)) != 0 || data.Length < pageSize)
                throw new InvalidDataException();
            int reserved = data[20];
            usableSize = pageSize - reserved;
            if (usableSize < 480) throw new InvalidDataException();
            int encoding = checked((int)ReadUInt32(56));
            textEncoding = encoding == 1 ? Encoding.UTF8 :
                encoding == 2 ? Encoding.Unicode :
                encoding == 3 ? Encoding.BigEndianUnicode : null;
            if (textEncoding == null) throw new InvalidDataException();
        }

        internal bool TryFindTable(string table, out int rootPage, out string sql)
        {
            rootPage = 0;
            sql = String.Empty;
            List<object[]> rows;
            if (!TryReadTable(1, out rows)) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                object[] row = rows[i];
                if (row.Length < 5 || !(row[0] is string) || !(row[1] is string) ||
                    !(row[4] is string)) continue;
                if (!String.Equals((string)row[0], "table",
                    StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals((string)row[1], table,
                    StringComparison.OrdinalIgnoreCase)) continue;
                long root;
                if (!TryInteger(row[3], out root) || root <= 0 || root > Int32.MaxValue)
                    return false;
                rootPage = (int)root;
                sql = (string)row[4];
                return true;
            }
            return false;
        }

        internal bool TryReadTable(int rootPage, out List<object[]> rows)
        {
            rows = new List<object[]>();
            List<SqliteRow> sqliteRows;
            if (!TryReadTableRows(rootPage, out sqliteRows)) return false;
            for (int i = 0; i < sqliteRows.Count; i++)
                rows.Add(sqliteRows[i].Values);
            return true;
        }

        internal bool TryReadTableRows(int rootPage, out List<SqliteRow> rows)
        {
            rows = new List<SqliteRow>();
            var visited = new HashSet<int>();
            return ReadTablePage(rootPage, rows, visited, 0);
        }

        bool ReadTablePage(int pageNumber, List<SqliteRow> rows,
            HashSet<int> visited, int depth)
        {
            if (pageNumber <= 0 || depth > 64 || !visited.Add(pageNumber)) return false;
            long pageLong = ((long)pageNumber - 1L) * pageSize;
            if (pageLong < 0 || pageLong + pageSize > data.Length) return false;
            int page = (int)pageLong;
            int header = page + (pageNumber == 1 ? 100 : 0);
            if (header + 12 > page + pageSize) return false;
            byte type = data[header];
            int cells = ReadUInt16(header + 3);
            int headerSize = type == 0x05 ? 12 : type == 0x0D ? 8 : 0;
            if (headerSize == 0 || cells < 0 || header + headerSize + cells * 2 >
                page + pageSize) return false;

            if (type == 0x05)
            {
                for (int i = 0; i < cells; i++)
                {
                    int cell = page + ReadUInt16(header + headerSize + i * 2);
                    if (cell < page || cell + 4 > page + pageSize) return false;
                    int child = checked((int)ReadUInt32(cell));
                    if (!ReadTablePage(child, rows, visited, depth + 1)) return false;
                }
                int right = checked((int)ReadUInt32(header + 8));
                return ReadTablePage(right, rows, visited, depth + 1);
            }

            for (int i = 0; i < cells; i++)
            {
                int cell = page + ReadUInt16(header + headerSize + i * 2);
                if (cell < page || cell >= page + pageSize) return false;
                int cursor = cell;
                ulong payloadSize;
                ulong rowId;
                if (!TryVarint(ref cursor, page + pageSize, out payloadSize) ||
                    !TryVarint(ref cursor, page + pageSize, out rowId) ||
                    payloadSize > Int32.MaxValue) return false;
                byte[] payload;
                if (!TryReadPayload(page, cursor, (int)payloadSize, out payload))
                    return false;
                object[] record;
                if (!TryRecord(payload, 0, payload.Length, out record)) return false;
                rows.Add(new SqliteRow(rowId, record));
            }
            return true;
        }

        bool TryReadPayload(int page, int cursor, int payloadSize,
            out byte[] payload)
        {
            payload = null;
            int maxLocal = usableSize - 35;
            int minLocal = ((usableSize - 12) * 32 / 255) - 23;
            int local = payloadSize;
            if (payloadSize > maxLocal)
            {
                int candidate = minLocal + (payloadSize - minLocal) %
                    (usableSize - 4);
                local = candidate <= maxLocal ? candidate : minLocal;
            }
            if (local < 0 || local > payloadSize || cursor < page ||
                cursor + (long)local > page + usableSize) return false;

            byte[] result = new byte[payloadSize];
            Buffer.BlockCopy(data, cursor, result, 0, local);
            int done = local;
            if (done < payloadSize)
            {
                if (cursor + local + 4 > page + usableSize) return false;
                int overflowPage = checked((int)ReadUInt32(cursor + local));
                var visited = new HashSet<int>();
                while (done < payloadSize)
                {
                    if (overflowPage <= 0 || !visited.Add(overflowPage)) return false;
                    long offsetLong = ((long)overflowPage - 1L) * pageSize;
                    if (offsetLong < 0 || offsetLong + usableSize > data.Length)
                        return false;
                    int offset = (int)offsetLong;
                    int take = Math.Min(payloadSize - done, usableSize - 4);
                    if (take <= 0) return false;
                    Buffer.BlockCopy(data, offset + 4, result, done, take);
                    done += take;
                    overflowPage = checked((int)ReadUInt32(offset));
                }
            }
            payload = result;
            return true;
        }

        bool TryRecord(byte[] buffer, int start, int length, out object[] record)
        {
            record = null;
            if (buffer == null || start < 0 || length <= 0 ||
                start + (long)length > buffer.Length)
                return false;
            int headerCursor = start;
            ulong headerSizeValue;
            if (!TryVarint(buffer, ref headerCursor, start + length,
                out headerSizeValue) ||
                headerSizeValue > (ulong)length) return false;
            int headerEnd = start + (int)headerSizeValue;
            if (headerCursor > headerEnd) return false;
            var serials = new List<ulong>();
            while (headerCursor < headerEnd)
            {
                ulong serial;
                if (!TryVarint(buffer, ref headerCursor, headerEnd, out serial))
                    return false;
                serials.Add(serial);
            }
            int body = headerEnd;
            var values = new object[serials.Count];
            for (int i = 0; i < serials.Count; i++)
            {
                int size = SerialSize(serials[i]);
                if (size < 0 || body + (long)size > start + length) return false;
                object value;
                if (!TrySerialValue(buffer, serials[i], body, size, out value))
                    return false;
                values[i] = value;
                body += size;
            }
            record = values;
            return true;
        }

        int SerialSize(ulong serial)
        {
            switch (serial)
            {
                case 0: return 0;
                case 1: return 1;
                case 2: return 2;
                case 3: return 3;
                case 4: return 4;
                case 5: return 6;
                case 6: return 8;
                case 7: return 8;
                case 8: return 0;
                case 9: return 0;
                case 10:
                case 11: return -1;
                default:
                    ulong length = serial % 2 == 0 ? (serial - 12) / 2 :
                        (serial - 13) / 2;
                    return length <= Int32.MaxValue ? (int)length : -1;
            }
        }

        bool TrySerialValue(byte[] buffer, ulong serial, int at, int size,
            out object value)
        {
            value = null;
            if (serial == 0) return true;
            if (serial >= 1 && serial <= 6)
            {
                long number = 0;
                for (int i = 0; i < size; i++)
                    number = (number << 8) | buffer[at + i];
                if (size < 8 && (buffer[at] & 0x80) != 0)
                    number |= -1L << (size * 8);
                value = number;
                return true;
            }
            if (serial == 7)
            {
                byte[] bytes = new byte[8];
                for (int i = 0; i < 8; i++) bytes[7 - i] = buffer[at + i];
                value = BitConverter.ToDouble(bytes, 0);
                return true;
            }
            if (serial == 8) { value = 0L; return true; }
            if (serial == 9) { value = 1L; return true; }
            if (serial >= 12 && serial % 2 == 0)
            {
                byte[] blob = new byte[size];
                Buffer.BlockCopy(buffer, at, blob, 0, size);
                value = blob;
                return true;
            }
            if (serial >= 13 && serial % 2 == 1)
            {
                value = textEncoding.GetString(buffer, at, size);
                return true;
            }
            return false;
        }

        bool TryVarint(ref int cursor, int end, out ulong value)
        {
            return TryVarint(data, ref cursor, end, out value);
        }

        static bool TryVarint(byte[] buffer, ref int cursor, int end,
            out ulong value)
        {
            value = 0;
            for (int i = 0; i < 9; i++)
            {
                if (cursor >= end) return false;
                byte b = buffer[cursor++];
                if (i == 8)
                {
                    value = (value << 8) | b;
                    return true;
                }
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        int ReadUInt16(int at)
        {
            if (at < 0 || at + 2 > data.Length) throw new InvalidDataException();
            return (data[at] << 8) | data[at + 1];
        }

        uint ReadUInt32(int at)
        {
            if (at < 0 || at + 4 > data.Length) throw new InvalidDataException();
            return ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
                ((uint)data[at + 2] << 8) | data[at + 3];
        }
    }
}
