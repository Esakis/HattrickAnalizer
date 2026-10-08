# Instrukcja uruchomienia

## Wymagania

- .NET 8 SDK
- Node.js 18 lub nowszy oraz npm
- Dane aplikacji CHPP tylko wtedy, gdy chcesz pobierać prawdziwe dane Hattricka

## Uruchomienie lokalne

1. Skopiuj `Backend/appsettings.example.json` do `Backend/appsettings.json`.
2. Wpisz klucz i sekret CHPP do lokalnego pliku. Nie dodawaj danych logowania do repozytorium.
3. W katalogu głównym uruchom API:

   ```powershell
   dotnet run --project Backend/HattrickAnalizer.csproj
   ```

4. W osobnym terminalu zainstaluj zależności i uruchom interfejs:

   ```powershell
   cd Frontend
   npm ci
   npm start
   ```

Adres API jest wypisany przez ASP.NET Core. Angular domyślnie działa pod `http://localhost:4200`; Swagger jest dostępny w środowisku Development pod `/swagger` na adresie API.

Tryb bez CHPP włączysz lokalnie przez `UseMockData=true`. Mock jest deterministyczny i nie wykonuje zapytań do Hattricka.

## Kompilacja i testy

Uruchom z katalogu głównego:

```powershell
dotnet build Backend/HattrickAnalizer.csproj --configuration Release
dotnet test Backend.Tests/Backend.Tests.csproj --configuration Release
cd Frontend
npm ci
npm run build
npm test -- --watch=false --browsers=ChromeHeadless
```

Testy backendu korzystają z mocków i lokalnych danych. Nie łączą się z CHPP i nie wysyłają ustawień meczu.

Optymalizator domyślnie maksymalizuje prawdopodobieństwo wygranej (`Win`) i automatycznie dobiera formację oraz taktykę. Ręcznie wskazana formacja lub taktyka pozostaje ograniczeniem wyszukiwania; dla nieznanego doświadczenia formacji stosowany jest jawny poziom zastępczy 5. Prognozy są niezweryfikowanymi estymacjami (`unvalidated-low`), a nie gwarancją wyniku.

Porównanie optymalizatora celu Win na stałych danych uruchom poleceniem `dotnet run --project Backend.Benchmarks/HattrickAnalizer.Benchmarks.csproj --configuration Release`. Opcja `-- --legacy7` uruchamia oryginalne siedem scenariuszy porównujących legalny skład początkowy z wynikiem optymalizacji. Opis i zapisane wyniki sprzed zmiany są w [dokumentacji benchmarku](docs/win-optimization-benchmark.md).

## Niepewność prognoz i kalibracja

Oceny sektorów i prawdopodobieństwa są prognozami modelu, a nie gwarancją wyniku. API zwraca wersję modelu, etykietę pewności, pochodzenie danych i ostrzeżenia. Etykieta `unvalidated-low` oznacza, że jakość prognoz nie została potwierdzona na rzeczywistych meczach. Testy regresji sprawdzają spójność i znane reguły, ale testy syntetyczne nie dowodzą poprawy trafności.

1. Przed meczem zapisz kompletną migawkę drużyny: dokładnie 11 zawodników, dostępne prywatne umiejętności, pozycje i zachowania, taktykę, postawę, trenera, ducha drużyny, pewność siebie, doświadczenie formacji, poziom asystenta, status gospodarza oraz pogodę. Endpoint: `POST /api/calibration/snapshots/capture`.
2. Migawka zarejestrowana po rozpoczęciu meczu, z niepełnym kontekstem lub z zawodnikiem spoza zapisanego składu nie jest używana do odtworzenia.
3. Po meczu pobierz `GET /api/calibration/own-matches?count=5`. Sprawdź liczbę próbek, wykluczenia, błędy treningowe i wyniki na zbiorze kontrolnym. Do odtworzenia nie używaj obecnego składu ani późniejszych zmian umiejętności zamiast zapisanej migawki.
4. Kalibracja dzieli mecze chronologicznie i odkłada najnowsze 20% (co najmniej jeden mecz, jeśli próbek jest więcej niż jedna) jako zbiór kontrolny. Zmiany modelu dobieraj na wcześniejszych meczach; nie stroić ich na zbiorze kontrolnym. Oceniaj kolejne mecze przed podniesieniem etykiety pewności lub deklaracją lepszej trafności.

Więcej szczegółów o ograniczeniach prognoz i procedurze znajduje się w [README.md](README.md).
