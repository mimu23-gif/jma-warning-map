using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JmaMap.Tools
{
    // ESRI シェープファイル（.shp）と属性ファイル（.dbf）の読み取り。
    // 仕様は公開されている固定長バイナリなので、標準機能だけで読める。
    //
    // 座標変換は行わない。気象庁の予報区等GISは JGD2011 の「地理座標（経緯度）」で配布されており、
    // JGD2011 と WGS84 の差はセンチメートル級。したがって EPSG:4326 として
    // そのまま出力してよい（Python版の to_crs(4326) も実質的に無変換）。
    // 投影座標系（PROJCS）のデータは扱えないため、.prj を見て弾く。

    public class ShpRecord
    {
        public int Number;
        public int ShapeType;
        public List<double[]> Rings;   // 各リング（線なら各パート）は [x0,y0,x1,y1,...]
        public bool IsNull;
        public bool IsLine;            // PolyLine 系なら true（津波予報区など海岸線のデータ）
    }

    public class ShpReader : IDisposable
    {
        public const int TypeNull = 0;
        public const int TypePolyLine = 3;
        public const int TypePolygon = 5;
        public const int TypePolyLineZ = 13;
        public const int TypePolygonZ = 15;
        public const int TypePolyLineM = 23;
        public const int TypePolygonM = 25;

        readonly FileStream fs;
        readonly long endOffset;
        public int FileShapeType;

        public ShpReader(string path)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            byte[] header = ReadExactly(fs, 100);

            int fileCode = ReadInt32BE(header, 0);
            if (fileCode != 9994) throw new FormatException("シェープファイルではありません（ファイルコード=" + fileCode.ToString(CultureInfo.InvariantCulture) + "）: " + path);

            int lengthWords = ReadInt32BE(header, 24);
            endOffset = (long)lengthWords * 2;
            FileShapeType = BitConverter.ToInt32(header, 32 + 4);
        }

        public void Dispose() { fs.Dispose(); }

        public ShpRecord Next()
        {
            if (fs.Position >= endOffset) return null;

            byte[] recHeader = ReadExactly(fs, 8);
            int number = ReadInt32BE(recHeader, 0);
            int contentWords = ReadInt32BE(recHeader, 4);
            int contentBytes = contentWords * 2;
            if (contentBytes <= 0) return new ShpRecord { Number = number, IsNull = true, ShapeType = TypeNull };

            byte[] content = ReadExactly(fs, contentBytes);
            int shapeType = BitConverter.ToInt32(content, 0);

            var rec = new ShpRecord();
            rec.Number = number;
            rec.ShapeType = shapeType;

            if (shapeType == TypeNull)
            {
                rec.IsNull = true;
                return rec;
            }
            // PolyLine と Polygon はレコードの並び（bbox・numParts・numPoints・parts・points）が同じで、
            // 各パートをリングとして閉じるか折れ線のまま扱うかだけが違う。
            bool isPolygon = (shapeType == TypePolygon || shapeType == TypePolygonZ || shapeType == TypePolygonM);
            bool isLine = (shapeType == TypePolyLine || shapeType == TypePolyLineZ || shapeType == TypePolyLineM);
            if (!isPolygon && !isLine)
                throw new NotSupportedException("ポリゴン・折れ線以外の図形型には対応していません（shapeType=" + shapeType.ToString(CultureInfo.InvariantCulture) + "）");
            rec.IsLine = isLine;

            // 4 doubles の bbox を読み飛ばした位置から
            int p = 4 + 32;
            int numParts = BitConverter.ToInt32(content, p); p += 4;
            int numPoints = BitConverter.ToInt32(content, p); p += 4;
            if (numParts <= 0 || numPoints <= 0) { rec.IsNull = true; return rec; }

            var parts = new int[numParts];
            for (int i = 0; i < numParts; i++) { parts[i] = BitConverter.ToInt32(content, p); p += 4; }

            int pointsOffset = p;
            rec.Rings = new List<double[]>(numParts);
            for (int i = 0; i < numParts; i++)
            {
                int start = parts[i];
                int end = (i + 1 < numParts) ? parts[i + 1] : numPoints;
                int n = end - start;
                if (n <= 0) continue;

                var ring = new double[n * 2];
                int off = pointsOffset + start * 16;
                for (int k = 0; k < n; k++)
                {
                    ring[k * 2] = BitConverter.ToDouble(content, off);          // X = 経度
                    ring[k * 2 + 1] = BitConverter.ToDouble(content, off + 8);  // Y = 緯度
                    off += 16;
                }
                rec.Rings.Add(ring);
            }
            // PolygonZ / PolygonM の Z・M 配列はこの後ろに続くが、レコード長で読み飛ばしている
            return rec;
        }

        static byte[] ReadExactly(Stream s, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException("シェープファイルが途中で終わっています");
                read += n;
            }
            return buf;
        }

        static int ReadInt32BE(byte[] b, int offset)
        {
            return (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
        }
    }

    public class DbfField
    {
        public string Name;
        public char Type;
        public int Length;
        public int Decimals;
    }

    public class DbfReader : IDisposable
    {
        readonly FileStream fs;
        readonly Encoding encoding;
        readonly int recordLength;
        public List<DbfField> Fields = new List<DbfField>();
        public int RecordCount;
        int readCount;

        public DbfReader(string path, Encoding encoding)
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var header = new byte[32];
            fs.Read(header, 0, 32);

            RecordCount = BitConverter.ToInt32(header, 4);
            int headerLength = BitConverter.ToInt16(header, 8);
            recordLength = BitConverter.ToInt16(header, 10);
            this.encoding = encoding;

            var desc = new byte[32];
            while (true)
            {
                int b = fs.ReadByte();
                if (b < 0 || b == 0x0D) break;    // フィールド定義の終端
                desc[0] = (byte)b;
                fs.Read(desc, 1, 31);

                int nameLen = 0;
                while (nameLen < 11 && desc[nameLen] != 0) nameLen++;

                var f = new DbfField();
                f.Name = Encoding.ASCII.GetString(desc, 0, nameLen).Trim();
                f.Type = (char)desc[11];
                f.Length = desc[16];
                f.Decimals = desc[17];
                Fields.Add(f);
            }
            fs.Position = headerLength;
        }

        public void Dispose() { fs.Dispose(); }

        /// <summary>1レコード分の値を返す（削除マーク付きは読み飛ばす）。終端で null。</summary>
        public string[] ReadRecord()
        {
            while (readCount < RecordCount)
            {
                var buf = new byte[recordLength];
                int read = 0;
                while (read < recordLength)
                {
                    int n = fs.Read(buf, read, recordLength - read);
                    if (n <= 0) return null;
                    read += n;
                }
                readCount++;

                if (buf[0] == 0x2A) continue;   // 削除済みレコード

                var values = new string[Fields.Count];
                int off = 1;
                for (int i = 0; i < Fields.Count; i++)
                {
                    int len = Fields[i].Length;
                    values[i] = encoding.GetString(buf, off, len).Trim().Trim('\0');
                    off += len;
                }
                return values;
            }
            return null;
        }

        /// <summary>
        /// DBFの文字コードを決める。
        /// 1) 同名の .cpg があればそれに従う（GDAL/OGRが書き出す符号化指定）
        /// 2) 無ければレコード領域だけをUTF-8として検証する
        ///    （ヘッダにはバイナリ値が入っていてUTF-8として不正になり得るので、判定に含めてはいけない）
        /// 3) それでも駄目ならCP932（気象庁配布のDBFはこちらが多い）
        /// </summary>
        public static Encoding DetectEncoding(string path)
        {
            string cpg = Path.ChangeExtension(path, ".cpg");
            if (File.Exists(cpg))
            {
                string name = File.ReadAllText(cpg, Encoding.ASCII).Trim();
                Encoding fromCpg = FromCodePageName(name);
                if (fromCpg != null) return fromCpg;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 32) return Encoding.GetEncoding(932);
            int headerLength = BitConverter.ToInt16(bytes, 8);
            if (headerLength <= 0 || headerLength >= bytes.Length) return Encoding.GetEncoding(932);

            try
            {
                new UTF8Encoding(false, true).GetString(bytes, headerLength, bytes.Length - headerLength);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(932);
            }
        }

        static Encoding FromCodePageName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.ToUpperInvariant();
            if (n.IndexOf("UTF-8", StringComparison.Ordinal) >= 0
                || n.IndexOf("UTF8", StringComparison.Ordinal) >= 0
                || n.IndexOf("65001", StringComparison.Ordinal) >= 0) return new UTF8Encoding(false);
            if (n.IndexOf("932", StringComparison.Ordinal) >= 0
                || n.IndexOf("SJIS", StringComparison.Ordinal) >= 0
                || n.IndexOf("SHIFT", StringComparison.Ordinal) >= 0) return Encoding.GetEncoding(932);
            try { return Encoding.GetEncoding(n); }
            catch (Exception) { return null; }
        }
    }

    public static class PrjCheck
    {
        /// <summary>.prj を読み、投影座標系なら例外。地理座標系ならそのまま使える。</summary>
        public static string Verify(string shpPath)
        {
            string prj = Path.ChangeExtension(shpPath, ".prj");
            if (!File.Exists(prj)) return "(.prj なし・地理座標として扱います)";

            string wkt = File.ReadAllText(prj, Encoding.ASCII).Trim();
            if (wkt.IndexOf("PROJCS", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new NotSupportedException(
                    "投影座標系（PROJCS）のシェープファイルには対応していません。" +
                    "本ツールは座標変換を行わないため、地理座標（経緯度）のデータを使ってください。");

            int i = wkt.IndexOf('"');
            int j = (i >= 0) ? wkt.IndexOf('"', i + 1) : -1;
            return (i >= 0 && j > i) ? wkt.Substring(i + 1, j - i - 1) : wkt;
        }
    }
}
