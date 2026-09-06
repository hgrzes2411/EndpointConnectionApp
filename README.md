# EndpointConnectionApp

Prosta aplikacja konsolowa pobierająca fakty o kotach z publicznego API i zapisująca je do pliku tekstowego.

Wymagania
- .NET Framework 4.8.1

Uruchomienie
1. Otwórz rozwiązanie w Visual Studio 2022/2026.
2. Uruchom projekt.
3. Naciśnij Enter, aby pobrać fakt i zapisać do pliku (domyślnie catfacts.txt).
4. Wpisz `Q` + Enter aby zakończyć.

Konfiguracja
- appsettings.json: FactApi:BaseUrl, File:Path, RequestTimeoutSeconds, RetryMaxAttempts, RetryDelayMs
Format zapisu w pliku:
- Każda linia zawiera tylko treść otrzymanego faktu (po jednej linii = sam tekst faktu)

Obsługa błędów
- API: timeout, HTTP 4xx/5xx, pusty/nieprawidłowy response — logowane i obsługiwane.
- Zapis pliku: IOException, UnauthorizedAccessException — logowane i zgłaszane użytkownikowi.

Design decisions
- Nie użyto zewnętrznych bibliotek (np. Microsoft.Extensions.*) aby uniknąć dodawania NuGetów. Zamiast tego prosty ILogger<T> i prosty DI container w pliku Program.cs.
- Konfiguracja w appsettings.json wczytywana prostym parserem — wystarcza dla prostych ustawień i nie wymaga dodatkowych pakietów.
- Retry implementowany prosto w serwisie API; retry dotyczy głównie błędów sieciowych i kodów 5xx.
