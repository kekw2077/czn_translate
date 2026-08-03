namespace CznTranslator.Ocr;

/// <summary>
/// Label set for the recognition head, built from a PaddleOCR dictionary file (<c>en_dict.txt</c>).
/// <para>
/// The file lists one character per line and carries neither the CTC blank nor the space. Paddle's
/// own post-processing prepends the blank at index 0 and appends a space at the end, so the same
/// has to happen here — get either wrong and every decoded string comes out shifted by one class.
/// </para>
/// </summary>
public sealed class CharacterDictionary
{
    public const int BlankIndex = 0;

    private readonly string[] _labels;

    private CharacterDictionary(string[] labels) => _labels = labels;

    public int Count => _labels.Length;

    public string this[int index] => _labels[index];

    public static CharacterDictionary Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Recognition dictionary not found at '{path}'.", path);

        return FromLines(File.ReadAllLines(path));
    }

    public static CharacterDictionary FromLines(IEnumerable<string> lines)
    {
        var labels = new List<string> { string.Empty };
        labels.AddRange(lines);
        labels.Add(" ");
        return new CharacterDictionary([.. labels]);
    }

    public bool IsBlank(int index) => index == BlankIndex;
}
