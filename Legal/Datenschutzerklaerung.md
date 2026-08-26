# Datenschutzerklärung — MyriaRPG

Stand: 2026-08-26

Diese Datenschutzerklärung informiert dich über Art, Umfang und Zweck der
Verarbeitung personenbezogener Daten bei Nutzung von MyriaRPG (WPF-Client,
Console-Client, Mono-Client, Auth-Server, Realm-Server; im Folgenden „das
Spiel"). Sie gilt gemäß der Datenschutz-Grundverordnung (DSGVO) und dem
österreichischen Datenschutzgesetz (DSG).

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

### 2.2 Spielstand-Daten (Realm-Server)

Für jeden erstellten Charakter werden Spielfortschrittsdaten gespeichert
(Level, Erfahrung, Inventar, Fertigkeiten, Gilden- und
Freundschaftszugehörigkeit u. Ä.). Diese Daten sind einem Account über eine
interne Kennung zugeordnet, enthalten aber selbst keine direkten
Identifikationsmerkmale außer dem selbst gewählten Charakternamen.

### 2.3 IP-Adressen

Deine IP-Adresse wird beim Zugriff auf die Server verarbeitet, wie es bei
jeder Netzwerkverbindung technisch notwendig ist. Am Auth-Server wird sie
zusätzlich kurzzeitig für ein Rate-Limiting (max. 5 Registrierungs-/
Login-Versuche pro Minute) im Arbeitsspeicher verwendet, um automatisierte
Angriffe (Brute-Force, Credential Stuffing) zu erschweren. Eine dauerhafte
Speicherung oder Protokollierung von IP-Adressen in einer Datenbank findet
nicht statt.

### 2.4 Keine Cookies, kein Tracking

Die Clients (WPF, Console, Mono) und die Server setzen keine Cookies, kein
Analytics- und kein Werbetracking ein.

### 2.5 Server-Logs und Backups

Die Server führen kein anwendungsseitiges Zugriffsprotokoll (kein Logging
von IP-Adressen, aufgerufenen Endpunkten oder Anfragen pro Nutzer:in). Es
wird lediglich eine minimale Startmeldung (Versionsnummer) auf der
Konsolenausgabe protokolliert — sowie, falls ein Realm-Server aus
betrieblichen Gründen ausnahmsweise unverschlüsselt (HTTP statt HTTPS)
angebunden wird, eine deutliche Warnmeldung beim Start und bei jedem
betroffenen internen Admin-Aufruf (siehe Abschnitt 2.7); auch das enthält
keine personenbezogenen Nutzerdaten, sondern nur die Serverkonfiguration
selbst. Wird ein Server als Systemdienst (systemd) betrieben, kann diese
Konsolenausgabe vom Betriebssystem im System-Journal mitprotokolliert
werden — das erfolgt durch das Betriebssystem, nicht durch die Anwendung
selbst, und unterliegt dessen Standard-Aufbewahrungsdauer.

Aktuell werden **keine Backups** der Account- und Spielstanddatenbanken
angefertigt. Sollte sich das ändern, wird diese Erklärung um Ort, Dauer und
Absicherung der Backups ergänzt.

### 2.6 Tunneling-Dienst (reiner Transit)

Die Verbindung zum Realm-Server läuft über einen TCP-Tunneling-Dienst
(playit.gg), da der Server nicht direkt über eine öffentliche IP-Adresse
erreichbar ist. Da die gesamte Kommunikation Ende-zu-Ende TLS-verschlüsselt
ist (siehe Abschnitt 2.7 „Übertragungssicherheit" unten), sieht dieser
Dienst ausschließlich verschlüsselte, für ihn nicht einsehbare Datenpakete
und kann daher keine personenbezogenen Inhalte (Zugangsdaten,
Spielstanddaten) mitlesen. Es handelt sich um eine reine
Transportvermittlung ohne inhaltlichen Zugriff, nicht um eine
Auftragsverarbeitung im Sinne von Art. 28 DSGVO.

### 2.7 Übertragungssicherheit

Sämtliche Kommunikation zwischen den Clients (WPF, Console, künftig Mono)
und den Servern (Login, Registrierung, Spielstand-Synchronisation) erfolgt
verschlüsselt über TLS (HTTPS). Da für die Server aktuell keine eigene
Domain existiert, kommt ein selbstsigniertes Zertifikat zum Einsatz. Die
Clients vertrauen einem solchen Zertifikat nach dem Prinzip
„Trust-on-First-Use" (TOFU, dasselbe Vertrauensmodell wie bei SSH
Host-Keys): Bei der ersten Verbindung zu einer bestimmten Server-Adresse
wird deren Zertifikats-Fingerabdruck lokal gespeichert; bei jeder weiteren
Verbindung zu derselben Adresse muss exakt dieses Zertifikat erneut
vorgelegt werden, sonst wird die Verbindung abgelehnt. Dieser lokal
gespeicherte Fingerabdruck verlässt das Gerät nie und wird an niemanden
übermittelt. Ein von einer öffentlichen Zertifizierungsstelle signiertes
Zertifikat wird davon unabhängig ganz normal validiert und benötigt dieses
Verfahren nicht — sobald für die Server eine eigene Domain vorhanden ist,
wird auf ein solches Zertifikat umgestellt.

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

## 3. Rechtsgrundlage der Verarbeitung

Die Verarbeitung der Account- und Spielstanddaten erfolgt zur Erfüllung
eines Vertrags bzw. vorvertraglicher Maßnahmen (Art. 6 Abs. 1 lit. b DSGVO),
konkret: um dir die Nutzung des Spiels (Login, Speicherung deines
Spielfortschritts) überhaupt zu ermöglichen. Das Rate-Limiting per IP-Adresse
stützt sich auf berechtigtes Interesse (Art. 6 Abs. 1 lit. f DSGVO) an der
Absicherung der Server gegen Missbrauch.

## 4. Speicherdauer

Account- und Charakterdaten werden gespeichert, solange der Account
besteht. Eine automatische Löschung nach Inaktivität ist derzeit nicht
implementiert.

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
  Mehrspieler-Menü des WPF-Clients ändern (Passwortänderung erfordert die
  Eingabe des aktuellen Passworts). Solltest du dein Passwort vergessen
  haben und dich daher nicht mehr einloggen können, gibt es mangels
  hinterlegter E-Mail-Adresse keine automatisierte Zurücksetzfunktion —
  wende dich in diesem Fall an die unten genannte Kontaktadresse; der
  Betreiber kann dein Passwort nach Identitätsprüfung manuell zurücksetzen
- **Löschung** (Art. 17 DSGVO) deines Accounts und der zugehörigen
  Spielstanddaten
- **Einschränkung der Verarbeitung** (Art. 18 DSGVO)
- **Datenübertragbarkeit** (Art. 20 DSGVO)
- **Widerspruch** (Art. 21 DSGVO) gegen Verarbeitungen, die auf berechtigtem
  Interesse beruhen (z. B. Rate-Limiting)
- **Beschwerde bei einer Aufsichtsbehörde**, in Österreich der
  [Datenschutzbehörde (DSB)](https://www.dsb.gv.at)

Die Account-Löschung ist im WPF-Client über „Account" im Mehrspieler-Menü
als Selbstbedienungsfunktion verfügbar (mit Sicherheitsabfrage vor dem
endgültigen Löschen) und ruft dabei denselben `DELETE /api/auth/account`-
Endpunkt auf: Der Auth-Server löscht zunächst alle Charaktere auf jedem
konfigurierten Realm-Server und anschließend den Account selbst. Der
Console-Client bietet diese Selbstbedienungsfunktion aktuell nicht direkt
an; nutze dafür entweder den WPF-Client oder wende dich formlos per E-Mail
an die oben genannte Adresse — in beiden Fällen betrifft die Löschung
denselben Account unabhängig davon, mit welchem Client er zuletzt genutzt
wurde.

## 7. Kontakt für Datenschutzanliegen

Für alle Fragen, Auskunfts- oder Löschbegehren wende dich an:
rllyben@proton.me

## 8. Änderungen dieser Erklärung

Diese Datenschutzerklärung kann bei Weiterentwicklung des Spiels (z. B. bei
Einführung neuer Funktionen wie E-Mail-Verifizierung, Cloud-Hosting oder
optionalen Zahlungen) angepasst werden. Die jeweils aktuelle Fassung ist im
`Legal/`-Ordner der jeweiligen Projekt-Repositories einsehbar.
