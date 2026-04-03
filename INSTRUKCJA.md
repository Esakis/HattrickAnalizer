# Instrukcja Uruchomienia - Hattrick Analyzer

## Krok 1: Konfiguracja Hattrick API

1. Zarejestruj się na https://www.hattrick.org/
2. Przejdź do CHPP (Hattrick API): https://chpp.hattrick.org/
3. Zarejestruj swoją aplikację i uzyskaj:
   - Consumer Key
   - Consumer Secret
   - Access Token
   - Access Token Secret

## Krok 2: Konfiguracja Backendu

1. Przejdź do folderu Backend:
   ```powershell
   cd D:\GryWebowe\HattrickAnalizer\Backend
   ```

2. Skopiuj plik konfiguracyjny:
   ```powershell
   copy appsettings.example.json appsettings.json
   ```

3. Edytuj `appsettings.json` i wpisz swoje dane API Hattrick:
   ```json
   {
     "HattrickApi": {
       "ConsumerKey": "TWOJ_CONSUMER_KEY",
       "ConsumerSecret": "TWOJ_CONSUMER_SECRET",
       "AccessToken": "TWOJ_ACCESS_TOKEN",
       "AccessTokenSecret": "TWOJ_ACCESS_TOKEN_SECRET"
     }
   }
   ```

4. Uruchom backend:
   ```powershell
   dotnet run
   ```

Backend będzie dostępny pod adresem: http://localhost:5000
Swagger UI: http://localhost:5000/swagger

## Krok 3: Uruchomienie Frontendu

1. Przejdź do folderu Frontend:
   ```powershell
   cd D:\GryWebowe\HattrickAnalizer\Frontend
   ```

2. Zainstaluj zależności (tylko przy pierwszym uruchomieniu):
   ```powershell
   npm install
   ```

3. Uruchom aplikację Angular:
   ```powershell
   npm start
   ```

Frontend będzie dostępny pod adresem: http://localhost:4200

## Krok 4: Użytkowanie

1. Otwórz przeglądarkę i przejdź do http://localhost:4200
2. Wpisz ID swojej drużyny (znajdziesz je w URL na Hattrick)
3. Wpisz ID drużyny przeciwnika
4. Wybierz preferowaną taktykę
5. Kliknij "Optymalizuj Skład"

## Funkcje Aplikacji

### ✅ Co aplikacja robi:

- **Pobiera dane drużyny** z API Hattrick (lub używa danych testowych)
- **Analizuje umiejętności zawodników** (obrona, rozgrywanie, strzelanie, itp.)
- **Generuje optymalny skład** na podstawie:
  - Umiejętności zawodników
  - Formy zawodników
  - Statystyk przeciwnika
  - Wybranej taktyki
- **Porównuje drużyny** i pokazuje:
  - Przewagę w środku pola
  - Siłę obrony i ataku
  - Mocne i słabe strony
- **Sugeruje taktykę** dostosowaną do przeciwnika

### 📊 Algorytm Optymalizacji

Aplikacja automatycznie:
1. Wybiera najlepszego bramkarza (najwyższa umiejętność bramkarza)
2. Ustawia 4 obrońców (najwyższe umiejętności obronne)
3. Ustawia 3 pomocników (najlepsze rozgrywanie + podania)
4. Ustawia 3 napastników (najwyższe umiejętności strzeleckie)
5. Oblicza przewidywane ratingi dla każdej strefy
6. Porównuje z przeciwnikiem i generuje rekomendacje

### 🎯 Rekomendacje Taktyczne

Na podstawie analizy aplikacja sugeruje:
- Czy grać ofensywnie czy defensywnie
- Którą flanką atakować
- Czy stawiać na rozgrywanie czy kontratak
- Gdzie są Twoje mocne strony
- Na co uważać (słabe punkty)

## Tryb Testowy

Jeśli nie masz dostępu do API Hattrick, aplikacja automatycznie wygeneruje:
- 18 losowych zawodników dla Twojej drużyny
- Losowe statystyki przeciwnika

Możesz przetestować wszystkie funkcje bez prawdziwego API!

## Rozwiązywanie Problemów

### Backend nie startuje
- Sprawdź czy masz zainstalowany .NET 8 SDK
- Uruchom: `dotnet --version`

### Frontend nie startuje
- Sprawdź czy masz zainstalowany Node.js
- Uruchom: `node --version` i `npm --version`

### Błąd CORS
- Upewnij się, że backend działa na porcie 5000
- Upewnij się, że frontend działa na porcie 4200

### Błąd API Hattrick
- Sprawdź czy dane API w `appsettings.json` są poprawne
- Aplikacja automatycznie przełączy się na dane testowe w przypadku błędu

## Technologie

- **Backend**: .NET 8, ASP.NET Core Web API
- **Frontend**: Angular 17, TypeScript, SCSS
- **API**: Hattrick CHPP (Community Helper Program Protocol)
