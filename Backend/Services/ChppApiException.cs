namespace HattrickAnalizer.Services;

// Błąd komunikacji z CHPP API (sieć, odrzucone żądanie, niepoprawny XML).
// Kontrolery mapują go na 502 Bad Gateway — nigdy nie podmieniamy danych na mockowe.
public class ChppApiException : Exception
{
    public ChppApiException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
