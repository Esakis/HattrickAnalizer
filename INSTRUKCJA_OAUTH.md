# Instrukcja OAuth - Hattrick Analyzer

## 🔐 Konfiguracja OAuth 1.0a dla Hattrick API

### Krok 1: Przygotowanie Kluczy API

1. **Zaloguj się** na https://www.hattrick.org/
2. **Przejdź do CHPP**:
   - Kliknij "Społeczność" → "Produkty CHPP"
   - Lub bezpośrednio: https://chpp.hattrick.org/
3. **Zarejestruj aplikację**:
   - Kliknij "Register new application"
   - Nazwa: `Hattrick Analyzer`
   - Opis: `Aplikacja do optymalizacji składu drużyny`
   - Callback URL: `oob` (out-of-band)
4. **Skopiuj klucze**:
   - Consumer Key
   - Consumer Secret

### Krok 2: Konfiguracja Backendu

1. Otwórz plik `Backend/appsettings.json`:
```json
{
  "HattrickApi": {
    "ConsumerKey": "TWOJ_CONSUMER_KEY",
    "ConsumerSecret": "TWOJ_CONSUMER_SECRET"
  }
}
```

2. Wklej swoje klucze z CHPP

### Krok 3: Uruchomienie Aplikacji

**Terminal 1 - Backend:**
```powershell
cd D:\GryWebowe\HattrickAnalizer\Backend
dotnet restore
dotnet run
```

Backend będzie dostępny na: http://localhost:5000

**Terminal 2 - Frontend:**
```powershell
cd D:\GryWebowe\HattrickAnalizer\Frontend
npm install
npm start
```

Frontend będzie dostępny na: http://localhost:4200

### Krok 4: Autoryzacja OAuth (w przeglądarce)

1. **Otwórz aplikację**: http://localhost:4200
2. **Przejdź do OAuth Setup**: http://localhost:4200/oauth-setup
3. **Kliknij "Rozpocznij autoryzację OAuth"**
4. **Otwórz link autoryzacji** (zostanie wygenerowany)
5. **Zaloguj się na Hattrick** (jeśli nie jesteś zalogowany)
6. **Zatwierdź dostęp** dla aplikacji
7. **Skopiuj kod PIN** który otrzymasz
8. **Wklej PIN** w aplikacji i kliknij "Zakończ autoryzację"

### Krok 5: Użytkowanie

Po pomyślnej autoryzacji:
1. Przejdź do głównej strony aplikacji
2. Wpisz ID swojej drużyny
3. Wpisz ID drużyny przeciwnika
4. Kliknij "Optymalizuj Skład"

Aplikacja automatycznie użyje OAuth do pobrania prawdziwych danych z Hattrick!

## 🔧 Testowanie OAuth

### Test przez Swagger (opcjonalnie)

1. Otwórz http://localhost:5000/swagger
2. Znajdź endpoint `/api/oauth/start`
3. Kliknij "Try it out" → "Execute"
4. Skopiuj `authorizationUrl` z odpowiedzi
5. Otwórz URL w przeglądarce i uzyskaj PIN
6. Użyj endpoint `/api/oauth/complete` z PIN

### Test przez cURL (opcjonalnie)

```powershell
# Krok 1: Start autoryzacji
curl http://localhost:5000/api/oauth/start

# Krok 2: Otwórz authorizationUrl w przeglądarce, uzyskaj PIN

# Krok 3: Zakończ autoryzację
curl -X POST http://localhost:5000/api/oauth/complete `
  -H "Content-Type: application/json" `
  -d '{"sessionId":"SESSION_ID","verifier":"PIN_CODE"}'

# Krok 4: Test połączenia
curl -X POST http://localhost:5000/api/oauth/test `
  -H "Content-Type: application/json" `
  -d '{"sessionId":"SESSION_ID","teamId":123456}'
```

## 📊 Jak Działa OAuth Flow

```
1. Aplikacja → Hattrick: "Chcę Request Token"
   ↓
2. Hattrick → Aplikacja: "Oto Request Token"
   ↓
3. Aplikacja → Użytkownik: "Otwórz ten URL i zatwierdź"
   ↓
4. Użytkownik → Hattrick: Logowanie i zatwierdzenie
   ↓
5. Hattrick → Użytkownik: "Oto PIN"
   ↓
6. Użytkownik → Aplikacja: Wpisuje PIN
   ↓
7. Aplikacja → Hattrick: "Wymień Request Token + PIN na Access Token"
   ↓
8. Hattrick → Aplikacja: "Oto Access Token"
   ↓
9. Aplikacja używa Access Token do wszystkich zapytań API
```

## 🎯 Endpointy API

### Backend Endpoints

- **GET** `/api/oauth/start` - Rozpocznij autoryzację
- **POST** `/api/oauth/complete` - Zakończ autoryzację (wymaga PIN)
- **GET** `/api/oauth/status/{sessionId}` - Sprawdź status sesji
- **POST** `/api/oauth/test` - Testuj połączenie z Hattrick

### Hattrick CHPP Endpoints

- **Request Token**: https://chpp.hattrick.org/oauth/request_token.ashx
- **Authorize**: https://chpp.hattrick.org/oauth/authorize.aspx
- **Access Token**: https://chpp.hattrick.org/oauth/access_token.ashx
- **Protected Resource**: https://chpp.hattrick.org/chppxml.ashx

## 🔍 Dostępne Pliki CHPP

Po autoryzacji możesz pobierać:

- `teamdetails` - Informacje o drużynie
- `players` - Lista zawodników
- `playerdetails` - Szczegóły zawodnika
- `matchdetails` - Szczegóły meczu
- `matches` - Lista meczów
- `economy` - Ekonomia drużyny
- `training` - Trening
- i wiele innych...

## ⚠️ Rozwiązywanie Problemów

### Backend nie startuje
```powershell
dotnet --version  # Sprawdź czy masz .NET 8
dotnet restore    # Zainstaluj pakiety
```

### Błąd OAuth Signature
- Sprawdź czy Consumer Key i Secret są poprawne
- Upewnij się, że nie ma spacji na początku/końcu kluczy
- Zegar systemowy musi być zsynchronizowany

### Błąd "Invalid Token"
- Token może wygasnąć - rozpocznij autoryzację od nowa
- PIN jest ważny tylko kilka minut

### Błąd CORS
- Upewnij się że backend działa na porcie 5000
- Upewnij się że frontend działa na porcie 4200

## 💡 Tryb Testowy (Bez OAuth)

Jeśli nie chcesz konfigurować OAuth, aplikacja działa w trybie testowym:
- Nie podawaj Session ID przy optymalizacji
- Aplikacja automatycznie wygeneruje dane testowe
- Wszystkie funkcje działają normalnie

## 📝 Notatki Techniczne

### Implementacja OAuth 1.0a
- Używamy HMAC-SHA1 do podpisywania zapytań
- Wszystkie zapytania używają metody GET
- Callback URL: `oob` (out-of-band, PIN-based)

### Bezpieczeństwo
- Consumer Secret i Token Secret nigdy nie są wysyłane w zapytaniach
- Używane są tylko do generowania podpisu
- Access Token jest przechowywany tylko w pamięci serwera

### Sesje
- Sesje są przechowywane w pamięci (Dictionary)
- W produkcji należy użyć Redis lub bazy danych
- Session ID jest generowany jako GUID
