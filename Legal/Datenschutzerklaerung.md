# Datenschutzerklärung — MyriaRPG

Stand: 2026-11-26

Diese Datenschutzerklärung informiert dich über Art, Umfang und Zweck der
Verarbeitung personenbezogener Daten bei Nutzung von MyriaRPG (WPF-Client,
Console-Client, Mono-Client, Auth-Server, Realm-Server sowie die Website
myria.myria-games.duckdns.org mit Downloads und automatischen Updates; im
Folgenden „das Spiel"). Sie gilt gemäß der Datenschutz-Grundverordnung (DSGVO)
und dem österreichischen Datenschutzgesetz (DSG).

> Der Mono-Client befindet sich aktuell in aktiver Entwicklung; die
> Mehrspieler-Anbindung (Login/Registrierung gegen den Auth-Server) ist zum
> Stand dieser Erklärung noch nicht implementiert. Sobald das der Fall ist,
> gilt für ihn exakt dieselbe Datenverarbeitung wie für den WPF- und den
> Console-Client, wie unten beschrieben.

## 1. Verantwortlicher

rllyben
E-Mail: rllyben@proton.me

MyriaRPG ist ein privates, nicht-kommerzielles Hobby-Projekt ohne
Erwerbsabsicht. Es werden keine Werbeeinnahmen erzielt, keine Bezahlfunktionen
angeboten und keine Daten an Dritte verkauft.

## 2. Welche Daten werden verarbeitet?

### 2.1 Account-Daten (Auth-Server)

Bei der Registrierung werden ausschließlich folgende Daten gespeichert:

| Feld | Zweck |
|---|---|
| Benutzername | Login-Identifikator, öffentlich sichtbar im Spiel |
| Passwort-Hash | Zur Authentifizierung; das Passwort selbst wird **nicht** gespeichert |
| Erstellungsdatum des Accounts | Interne Verwaltung |

Es wird **keine E-Mail-Adresse, kein Klarname und keine sonstige
Kontaktinformation** bei der Registrierung erhoben. Passwörter werden
mittels PBKDF2 (HMAC-SHA512, 200.000 Iterationen) mit individuellem Salt und
einem serverseitigen Pepper gehasht; ein Rückschluss auf das
Klartext-Passwort ist damit nicht möglich.

Registrierung, Anmeldung, Passwortänderung und Account-Löschung sind außer
in den Clients auch auf der Website (Seite „Account") möglich; dabei werden
dieselben Daten an denselben Auth-Server übermittelt und dort genauso
verarbeitet.

### 2.1a Anfragen zum Zurücksetzen des Passworts

Hast du dein Passwort vergessen, kannst du auf der Website (Seite „Account" →
„Passwort vergessen") ein Zurücksetzen anfragen. Dabei werden gespeichert:

| Feld | Zweck |
|---|---|
| Benutzername (Zuordnung zum Account) | Damit der Betreiber weiß, um welchen Account es geht |
| Von dir angegebene E-Mail-Adresse | Damit der Betreiber dich kontaktieren, deine Identität prüfen und dir den einmaligen Link zum Setzen eines neuen Passworts schicken kann |
| Zeitpunkt der Anfrage und Bearbeitungsstatus | Interne Verwaltung, Ablauf des Links |

Die E-Mail-Adresse wird **nur bei dieser Anfrage** gespeichert und **nicht**
mit deinem Account verknüpft oder für andere Zwecke (z. B. Newsletter)
verwendet. Sie wird automatisch gelöscht, sobald die Anfrage erledigt ist
(Passwort zurückgesetzt oder Anfrage verworfen), spätestens aber 30 Tage nach
der Anfrage. Gibt es keinen Account mit dem angegebenen Benutzernamen, wird
nichts gespeichert. Der Link selbst wird nur als kryptografischer Hashwert
gespeichert. Der Betreiber beantwortet die Anfrage per E-Mail aus seinem
eigenen E-Mail-Postfach; dabei wird die Nachricht über die Mailserver des
E-Mail-Anbieters des Betreibers und deines E-Mail-Anbieters übertragen.

### 2.2 Spielstand-Daten (Realm-Server)

Für jeden erstellten Charakter werden Spielfortschrittsdaten gespeichert
(Level, Erfahrung, Inventar, Fertigkeiten, Gilden- und
Freundschaftszugehörigkeit u. Ä.). Diese Daten sind einem Account über eine
interne Kennung zugeordnet, enthalten aber selbst keine direkten
Identifikationsmerkmale außer dem selbst gewählten Charakternamen.

### 2.3 IP-Adressen

Deine IP-Adresse wird beim Zugriff auf die Server verarbeitet, wie es bei
jeder Netzwerkverbindung technisch notwendig ist. Am Auth-Server wird sie
zusätzlich kurzzeitig für ein Rate-Limiting (max. 5 Anfragen an
Registrierung, Login und die übrigen Account-Funktionen pro Minute) im
Arbeitsspeicher verwendet, um automatisierte
Angriffe (Brute-Force, Credential Stuffing) zu erschweren. IP-Adressen werden
weder gespeichert noch in ein Protokoll geschrieben.

### 2.4 Keine Cookies, kein Tracking

Die Clients (WPF, Console, Mono) und die Server setzen keine Cookies, kein
Analytics- und kein Werbetracking ein.

### 2.5 Server-Logs und Backups

Auth- und Realm-Server schreiben ein Betriebsprotokoll, das das
Betriebssystem des Servers in seinem System-Journal aufbewahrt. Es enthält:

- Start- und Konfigurationsmeldungen (z. B. Versionsnummer, Warnungen zur
  Serverkonfiguration);
- Sicherheitsereignisse: Anmeldungen (erfolgreich und fehlgeschlagen),
  Registrierungen, Passwortänderungen und Passwort-Zurücksetzungen,
  Account-Löschungen und Umbenennungen sowie abgewiesene Versuche — etwa zu
  viele falsche Passwörter, ungültige oder widerrufene Sitzungs-Token,
  Versuche, den Charakter einer anderen Person zu verwenden, oder abgewiesene
  administrative Aufrufe;
- Spielereignisse: Verbinden und Trennen, Handel, Käufe und Verkäufe,
  Kämpfe sowie Gruppen- und Gildenaktionen.

Jeder Eintrag enthält den Zeitpunkt, die Art des Ereignisses, deinen
Benutzernamen und/oder Charakternamen und das Ergebnis. **Passwörter,
Sitzungs-Token, Links zum Zurücksetzen, Chatnachrichten und IP-Adressen werden
nie protokolliert.** Das Protokoll dient dem Betrieb der Server, der
Fehlersuche sowie dem Erkennen und Aufklären von Missbrauch und Angriffen
(z. B. dem Erraten von Passwörtern). Zugriff hat nur der Betreiber; Einträge
werden nach **30 Tagen** automatisch gelöscht. Der den Servern vorgeschaltete
Webserver (Reverse Proxy, siehe Abschnitt 2.6) führt kein Zugriffsprotokoll.

Aktionen, die der Betreiber im Verwaltungswerkzeug vornimmt (z. B. Sperren,
Rauswürfe, Account-Löschungen, ausgestellte Links zum Zurücksetzen des
Passworts), werden in einem eigenen Betreiberprotokoll mit Zeitpunkt, Aktion
und betroffenem Benutzernamen festgehalten, damit sie später nachvollziehbar
sind. Es enthält keine Passwörter oder Links, Zugriff hat nur der Betreiber,
und Einträge werden nach **einem Jahr** automatisch gelöscht.

Die Account- und Spielstanddatenbanken werden täglich automatisch gesichert.
Die Sicherungen liegen auf derselben Hardware des Betreibers (siehe
Abschnitt 5), sind ausschließlich für den Betreiber zugänglich und werden
nach 14 Tagen automatisch gelöscht. Wird ein Account oder Charakter
gelöscht, sind dessen Daten daher spätestens 14 Tage später auch aus allen
Sicherungen entfernt.

### 2.6 Domain, Namensauflösung und Reverse Proxy

Die Server sind direkt über die Domain myria.myria-games.duckdns.org auf der
Hardware des Betreibers erreichbar; ein Tunneling-Dienst wird nicht mehr
verwendet. Die Namensauflösung (Zuordnung des Domainnamens zur aktuellen
IP-Adresse) erfolgt über den DNS-Dienst DuckDNS; dabei werden keine Inhalte
deiner Verbindung an DuckDNS übertragen. Eingehende Verbindungen nimmt ein
Webserver (Reverse Proxy) auf derselben Hardware entgegen, entschlüsselt sie
und leitet sie lokal an Auth- bzw. Realm-Server weiter.

### 2.7 Übertragungssicherheit

Sämtliche Kommunikation zwischen den Clients (WPF, Console, künftig Mono)
bzw. der Website und den Servern (Login, Registrierung,
Spielstand-Synchronisation, Downloads, Updates) erfolgt verschlüsselt über
TLS (HTTPS) mit einem Zertifikat der öffentlichen Zertifizierungsstelle
Let's Encrypt, das von Clients und Browsern ganz normal validiert wird.

Wer einen eigenen Server ohne öffentlich signiertes Zertifikat betreibt,
kann ein selbstsigniertes Zertifikat verwenden. Die Clients vertrauen einem
solchen Zertifikat nach dem Prinzip „Trust-on-First-Use" (TOFU, dasselbe
Vertrauensmodell wie bei SSH Host-Keys): Bei der ersten Verbindung zu einer
bestimmten Server-Adresse wird deren Zertifikats-Fingerabdruck lokal
gespeichert; bei jeder weiteren Verbindung zu derselben Adresse muss exakt
dieses Zertifikat erneut vorgelegt werden, sonst wird die Verbindung
abgelehnt. Dieser lokal gespeicherte Fingerabdruck verlässt das Gerät nie
und wird an niemanden übermittelt.

Aus rein betrieblichen Gründen (z. B. ein Realm-Server, der noch nicht
hinter HTTPS läuft) ist eine Verbindung über unverschlüsseltes HTTP
technisch weiterhin möglich; die Clients weichen dafür nie stillschweigend
von HTTPS ab — außer bei einer Verbindung zu deinem eigenen Rechner
(„localhost") musst du eine `http://`-Adresse explizit selbst eingeben. Der
Auth-Server warnt zusätzlich unübersehbar (Konsolenausgabe beim Start sowie
bei jedem betroffenen internen Admin-Aufruf, siehe Abschnitt 2.5), wenn ein
konfigurierter Realm-Server unverschlüsselt erreichbar ist, da dabei ein
interner Authentifizierungs-Schlüssel zwischen Auth- und Realm-Server im
Klartext übertragen würde.

### 2.8 Website, Downloads und automatische Updates

Die Website besteht aus statischen Seiten. Sie setzt keine Cookies, nutzt
kein Tracking und lädt keine Inhalte von Drittanbietern (keine externen
Schriftarten, Skripte oder Bilder). Wählst du auf der Website eine Sprache,
wird diese Auswahl ausschließlich lokal in deinem Browser gespeichert
(„Local Storage") und nicht übertragen. Meldest du dich auf der Seite
„Account" an, wird nur dein Benutzername für die Dauer des Browser-Tabs im
Sitzungsspeicher deines Browsers („Session Storage") abgelegt, damit die
Seite weiß, für welchen Account du Änderungen vornimmst; dein Passwort und
Anmelde-Tokens werden dort nicht gespeichert, und der Eintrag wird beim
Abmelden oder Schließen des Tabs gelöscht. Den Status der Reiche (online,
Anzahl der Spieler:innen) ruft die Website direkt vom Server ab; dabei
werden keine Daten über dich übermittelt.

Der WPF-Client prüft beim Start, ob auf dem Server eine neuere Version
bereitsteht, und lädt diese gegebenenfalls herunter. Dabei wird — wie bei
jedem Download — deine IP-Adresse technisch verarbeitet, aber nicht
gespeichert oder protokolliert. Heruntergeladene Updates werden vor der
Installation anhand der digitalen Signatur des Betreibers geprüft.

## 3. Rechtsgrundlage der Verarbeitung

Die Verarbeitung der Account- und Spielstanddaten erfolgt zur Erfüllung
eines Vertrags bzw. vorvertraglicher Maßnahmen (Art. 6 Abs. 1 lit. b DSGVO),
konkret: um dir die Nutzung des Spiels (Login, Speicherung deines
Spielfortschritts) überhaupt zu ermöglichen. Das gilt auch für Anfragen zum
Zurücksetzen des Passworts (Abschnitt 2.1a), die dir den Zugang zu deinem
Account wieder ermöglichen sollen. Das Rate-Limiting per IP-Adresse und das
Betriebsprotokoll (Abschnitt 2.5) stützen sich auf berechtigtes Interesse
(Art. 6 Abs. 1 lit. f DSGVO) an einem sicheren und stabilen Betrieb der Server
und am Schutz der Server und der Accounts der Spieler:innen vor Missbrauch.

## 4. Speicherdauer

Account- und Charakterdaten werden gespeichert, solange der Account
besteht. Eine automatische Löschung nach Inaktivität ist derzeit nicht
implementiert. Die bei einer Anfrage zum Zurücksetzen des Passworts
angegebene E-Mail-Adresse wird gelöscht, sobald die Anfrage erledigt ist,
spätestens 30 Tage nach der Anfrage (zuzüglich höchstens 14 Tagen in den
Sicherungen, siehe Abschnitt 2.5). Einträge im Betriebsprotokoll werden nach
30 Tagen gelöscht (Abschnitt 2.5). Einträge im Betreiberprotokoll werden nach
einem Jahr gelöscht (Abschnitt 2.5).

## 5. Weitergabe an Dritte

Es findet keine Weitergabe deiner Daten an Dritte statt. Die Server (Auth-
und Realm-Server) laufen auf eigener Hardware des Betreibers, die sich in
Österreich befindet. Es kommt somit kein externer Hosting-Anbieter zum
Einsatz, der als Auftragsverarbeiter (Art. 28 DSGVO) einzubinden wäre — der
Betreiber verarbeitet die Daten selbst, physisch innerhalb der EU/des EWR.
Eine Drittstaatenübermittlung (Art. 44 ff. DSGVO) findet nicht statt.

## 6. Deine Rechte

Nach der DSGVO stehen dir insbesondere folgende Rechte zu:

- **Auskunft** (Art. 15 DSGVO) über die zu deinem Account gespeicherten Daten
- **Berichtigung** (Art. 16 DSGVO) unrichtiger Daten — Benutzername und
  Passwort kannst du jederzeit selbst über „Account" im
  Mehrspieler-Menü des WPF-Clients ändern, das Passwort auch auf der
  Website-Seite „Account" (Passwortänderung erfordert die Eingabe des
  aktuellen Passworts). Solltest du dein Passwort vergessen haben, kannst
  du auf der Website ein Zurücksetzen anfragen (siehe Abschnitt 2.1a) oder
  dich an die unten genannte Kontaktadresse wenden; der Betreiber setzt
  dein Passwort nach Identitätsprüfung zurück
- **Löschung** (Art. 17 DSGVO) deines Accounts und der zugehörigen
  Spielstanddaten
- **Einschränkung der Verarbeitung** (Art. 18 DSGVO)
- **Datenübertragbarkeit** (Art. 20 DSGVO)
- **Widerspruch** (Art. 21 DSGVO) gegen Verarbeitungen, die auf berechtigtem
  Interesse beruhen (z. B. Rate-Limiting)
- **Beschwerde bei einer Aufsichtsbehörde**, in Österreich der
  [Datenschutzbehörde (DSB)](https://www.dsb.gv.at)

Die Account-Löschung ist im WPF-Client über „Account" im Mehrspieler-Menü
sowie auf der Website-Seite „Account" als Selbstbedienungsfunktion verfügbar
(mit Sicherheitsabfrage vor dem endgültigen Löschen) und ruft dabei denselben `DELETE /api/auth/account`-
Endpunkt auf: Der Auth-Server löscht zunächst alle Charaktere auf jedem
konfigurierten Realm-Server und anschließend den Account selbst. Der
Console-Client bietet diese Selbstbedienungsfunktion aktuell nicht direkt
an; nutze dafür entweder den WPF-Client, die Website oder wende dich formlos per E-Mail
an die oben genannte Adresse — in beiden Fällen betrifft die Löschung
denselben Account unabhängig davon, mit welchem Client er zuletzt genutzt
wurde.

## 7. Kontakt für Datenschutzanliegen

Für alle Fragen, Auskunfts- oder Löschbegehren wende dich an:
rllyben@proton.me

## 8. Änderungen dieser Erklärung

Diese Datenschutzerklärung kann bei Weiterentwicklung des Spiels (z. B. bei
Einführung neuer Funktionen wie E-Mail-Verifizierung, Cloud-Hosting oder
optionalen Zahlungen) angepasst werden. Die jeweils aktuelle Fassung ist auf
der Website unter „Datenschutz" sowie im `Legal/`-Ordner der jeweiligen
Projekt-Repositories einsehbar.
