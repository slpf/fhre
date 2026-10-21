namespace FH6RB.Services;

public static class Fh3FileNameCodec
{
    private const string Logical = "abcdefghijklmnopqrstuvwxyz_-0123456789";
    private const string Physical = "l`^6qv{@$7sbg8huf4~1='m]!,y_[0wk(2j};+";

    public static string Encode(string value) => Transform(value, Logical, Physical, lowerCase: true);

    public static string Decode(string value) => Transform(value, Physical, Logical, lowerCase: false);

    private static string Transform(string value, string source, string target, bool lowerCase)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, (value, source, target, lowerCase), static (buffer, state) =>
        {
            for (var i = 0; i < state.value.Length; i++)
            {
                var c = state.value[i];
                if (state.lowerCase && c is >= 'A' and <= 'Z') c = (char) (c + ('a' - 'A'));
                var index = state.source.IndexOf(c);
                buffer[i] = index >= 0 ? state.target[index] : c;
            }
        });
    }
}
