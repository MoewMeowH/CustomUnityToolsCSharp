using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Wic = System.Windows.Media.Imaging;

namespace HB.Chat.Desktop;

/// <summary>
/// 읽은 스프라이트 시트 (계획서 2026-10-29_바탕화면-펫): WebP를 WIC로 읽어 미리 곱한 ARGB 비트맵으로 들고, 칸마다 보이는 점이 있는지(빈 칸 건너뛰기 —
/// CodePet frameOccupancy)를 한 번 센다.
/// </summary>
public sealed class PetSprite : IDisposable
{
    /// <summary>시트 파일 · 한 변 상한 (CodePet 기본 1536×2288 · 1.7MB의 넉넉한 몇 배).</summary>
    public const int MaxSide = 8192;
    public const long MaxFileBytes = 32L * 1024 * 1024;

    public PetSheet Sheet { get; }
    public Bitmap Image { get; }
    private readonly bool[,] _occupied;

    private PetSprite(PetSheet sheet, Bitmap image, bool[,] occupied) => (Sheet, Image, _occupied) = (sheet, image, occupied);

    public bool Occupied(int row, int column) => row < _occupied.GetLength(0) && column < _occupied.GetLength(1) && _occupied[row, column];

    /// <summary>그 동작에서 보일 칸들 (빈 칸을 뺀 프레임 순서). 하나도 없으면 쉼 행의 첫 칸.</summary>
    public IReadOnlyList<int> Frames(PetAction action)
    {
        var info = PetActions.Info(action, Sheet.Rows);
        var columns = Enumerable.Range(0, Math.Min(info.Frames, PetSheet.Columns)).Where(c => Occupied(info.Row, c)).ToList();
        return columns.Count > 0 ? columns : [0];
    }

    /// <summary>시트 파일을 읽는다. 디코더가 없거나 규격이 아니면 예외 (부른 쪽이 그 펫을 빼고 로그에 남긴다).</summary>
    public static PetSprite Load(string path, int? version)
    {
        // 크기는 픽셀을 풀기 전에 본다 — 밖 폴더(~/.codex/pets · 데이터 pets)의 큰 파일을 다 풀어 놓은 뒤 거절하지 않게 (지휘자 리뷰 K1)
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException($"시트 파일이 너무 큽니다 ({new FileInfo(path).Length / 1024 / 1024}MB).");
        using var stream = File.OpenRead(path);
        var decoder = Wic.BitmapDecoder.Create(stream, Wic.BitmapCreateOptions.PreservePixelFormat, Wic.BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > MaxSide || frame.PixelHeight > MaxSide) throw new InvalidDataException($"시트가 너무 큽니다 ({frame.PixelWidth}×{frame.PixelHeight}).");
        var pbgra = new Wic.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        int width = pbgra.PixelWidth, height = pbgra.PixelHeight;
        var sheet = PetSheet.Detect(width, height, version) ?? throw new InvalidDataException("빈 그림입니다.");
        var stride = width * 4;
        var pixels = new byte[stride * height];
        pbgra.CopyPixels(pixels, stride, 0);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        return new PetSprite(sheet, bitmap, Occupancy(pixels, stride, sheet));
    }

    /// <summary>칸마다 알파가 있는 점이 하나라도 있는지 (4점 간격으로 훑는다 — 그림 한 장에 한 번).</summary>
    internal static bool[,] Occupancy(byte[] pbgra, int stride, PetSheet sheet)
    {
        var occupied = new bool[sheet.Rows, PetSheet.Columns];
        for (var row = 0; row < sheet.Rows; row++)
            for (var column = 0; column < PetSheet.Columns; column++)
            {
                var cell = sheet.Cell(row, column);
                for (var y = cell.Top; y < cell.Bottom && !occupied[row, column]; y += 4)
                    for (var x = cell.Left; x < cell.Right; x += 4)
                        if (pbgra[y * stride + x * 4 + 3] > 8) { occupied[row, column] = true; break; }
            }
        return occupied;
    }

    public void Dispose() => Image.Dispose();
}
