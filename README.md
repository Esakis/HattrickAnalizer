# Hattrick Analyzer

Aplikacja do analizy i optymalizacji składu drużyny w grze Hattrick.

## Technologie
- Backend: .NET 8 Web API
- Frontend: Angular 17
- Integracja: Hattrick CHPP API

## Wymagania
- .NET 8 SDK
- Node.js 18+
- Klucz API Hattrick CHPP

## Konfiguracja

### Backend
1. Przejdź do folderu `Backend`
2. Skopiuj `appsettings.example.json` do `appsettings.json`
3. Uzupełnij dane API Hattrick
4. Uruchom: `dotnet run`

### Frontend
1. Przejdź do folderu `Frontend`
2. Zainstaluj zależności: `npm install`
3. Uruchom: `npm start`

## Funkcjonalności
- Pobieranie danych drużyny z API Hattrick
- Analiza statystyk zawodników
- Analiza przeciwnika
- Optymalizacja składu pod konkretnego przeciwnika
- Sugestie taktyczne
