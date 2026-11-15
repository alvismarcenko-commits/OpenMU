# AetherMU Europe — Grand Reset PostgreSQL kandidāts

Datums: 03.11.2026. Režīms: izolēts izstrādes kandidāts; nav atļaujas produkcijas integrācijai.

## Secinājums

PostgreSQL transakcijas robeža un septiņi īstas datubāzes scenāriji ir izstrādāti un kompilēti. Lokāli pārbaudītas 37 unikālas pārbaudes: 37 PASS, 0 FAIL. Septiņi PostgreSQL scenāriji ir NOT EXECUTED / SKIPPED, nevis PASS. Kandidāts ir gatavs izolētai PostgreSQL pārbaudei, bet tā datubāzes integrācija vēl nav pierādīta.

## Izmantotā bāze

- Lietotāja pievienotais GrandReset-Full-Audit-20261103-091609(1).txt, SHA-256 `2a2cc26e40c10c2e1098efa19185033fc309e9675444a2f948d540989ea1e69a`.
- GitHub OpenMU atzara galotne: `094f435606b1744b9049496bbce03b914860dd0d`. Tajā vēl bija vecā GR versija ar līmeni 10.
- Tādēļ atkārtoti izmantots iepriekšējais līmeņa 1 kandidāts `AetherMU-Europe-GrandReset-Level1-Candidate-20261102.zip`, SHA-256 `06b779d5ae556351d1a43c4c36b0105d1254534dee3eb3a6dec6a9a2a19e4ccc`.
- Atjaunota tā bundle ķēde `094f4356 → a275cfd4 → f9163df0`; jaunais darbs veikts savā `feature/aether-gr-postgresql-20261103` atzarā.
- Pievienotais audits ir avota pārskats un iepriekšējā Windows testa pierādījums; tas nesatur pilnu jaunāku pirmkodu. Pakotnes bāze nav apgalvota par baitu līmenī identisku lietotāja staging kopijai.
- Nemainītais initialization tests nav atkārtoti palaists. Spēles testi atkārtoti palaisti, jo ir mainīts GR izpildes ceļš.

## Faktiski izstrādātais kodā

`IAtomicCharacterProgressContext` nodala datubāzes transakciju no spēles loģikas. PostgreSQL `PlayerContext.SaveCharacterExchangeAsync`:

1. Paņem konteksta atslēgu un PostgreSQL transakciju.
2. Bloķē attiecīgā Character rindu ar `FOR UPDATE`.
3. Salīdzina datubāzē saglabāto EXP, pieejamos punktus un visu rakstura statistiku ar EF sākotnējo momentuzņēmumu. Izmanto sākotnējās, nevis tikko mainītās vērtības — derīgas vēl nesaglabātas spēles izmaiņas netiek kļūdaini salīdzinātas ar DB.
4. Saglabā visu izmaiņu kopu vienā transakcijā; EF izmaiņas pieņem tikai pēc komita. Automātiska nenoteikta komita atkārtošana nenotiek.
5. Novecojuša momentuzņēmuma, SQL/saglabāšanas kļūmes vai pazaudētas komita atbildes gadījumā invalidē kontekstu. Arī parastā un periodiskā saglabāšana pārbauda šo stāvokli zem konteksta atslēgas; atkoptais lokālais momentuzņēmums nevar pārrakstīt iespējami veiksmīgu komitu.

GrandResetCharacterAction izmanto šo iespēju pirms veiksmes paziņošanas. Kļūmes gadījumā atjauno lokālo momentuzņēmumu un atslēdz sesiju; nākamā sesija ielasa DB faktisko stāvokli. In-memory konteksts saglabā esošo SaveProgressAsync ceļu. Jaunu tabulu, kolonnu vai destruktīvu migrāciju nav.

## Faktiskie testi

| Pārbaude | PASS | FAIL | Izlaisti |
|---|---:|---:|---:|
| GrandResetCalculatorTest | 7 | 0 | 0 |
| GrandResetCharacterActionTest, ieskaitot četrus jaunus scenārijus | 21 | 0 | 0 |
| PostgreSQL drošības/EF modeļa tests | 8 | 0 | 0 |
| x1/x100 konfigurāciju semantika | 1 | 0 | 0 |
| Īsts PostgreSQL | 0 | 0 | 7 |
| Kopā, unikāli scenāriji | 37 | 0 | 7 |

`evidence/grand-reset.trx`: 36 PASS / 0 FAIL / 7 NOT EXECUTED. `evidence/profiles.trx`: 1 PASS. Agrākais `safety.trx` ir astoņu jau ieskaitīto drošības pārbaužu atsevišķais skrējiens; to nepieskaita vēlreiz.

Release pilnā testa projekta būvēšana iziet. Gala izolētā GameLogic būvēšana: 0 kļūdu, 26 esoši brīdinājumi; gala testa projekta būvēšana: 0 kļūdu, 5 esoši brīdinājumi. Jaunajos testu un transakcijas failos kompilatora brīdinājumi nav konstatēti. Žurnāli pievienoti.

Septiņi DB scenāriji pārbauda reālo EF AccountContext/Character/StatAttribute/ItemStorage/Item modeli un produkcijas transakcijas metodi:

- Pirmā, 20., 21. un 100. GR saglabāšana; konteksts aizvērts un atvērts no jauna; inventāra un ekipējuma ID, slots, līmenis un izturība saglabājas.
- Astoņi neatkarīgi, iepriekš ielasīti konteksti: viens komits, pārējie noraidīti, nav otra bonusa.
- Kļūme pēc SQL izpildes, bet pirms transakcijas komita: pilns rollback un nākamo saglabāšanu aizliegums.
- Mākslīga pazaudēta atbilde pēc veiksmīga komita: DB saglabā iegūto GR, invalidētais konteksts nedrīkst to pārrakstīt.

DB fixture sagatavo apmaiņas izmaiņu kopu un testē transakcijas robežu. Spēles darbības deleģēšana šai robežai un atslēgšanās pēc kļūmes pārbaudīta atsevišķā automatizētā action testā. Tas vēl nav īsts Windows spēles klienta gala tests vai PostgreSQL servera procesa restartēšanas tests.

## Atbilstība noteikumiem

100 vai vairāk Reset dod vienu GR; Reset kļūst 0, līmenis 1, EXP 0. Pastāvīgais punktu fonds `min(GR × 100, 2000)`. Parastais Reset saglabā šo bonusu — kopējie parastā Reset punkti drīkst pārsniegt 2000, GR daļa nedrīkst. x1 GR atspējots; x100 ieslēgts, līmenis pēc GR 1, EXP/Master EXP x100. Šie spēles noteikumi un profilu semantika ir lokāli pārbaudīti.

Spēles GR limits nav ieviests, tomēr esošais StatAttribute glabā skaitu kā `float`. Pēc 16 777 216 vienības pieaugums vairs nav precīzi attēlojams. Jauna pārbaude šādā gadījumā noraida GR, nepatērējot Reset. Matemātiski neierobežotam skaitītājam vajadzīga atsevišķa persistētā datu tipa maiņa; tā šajā darbā nav veikta. Arī daļskaitļa skaitītājs tiek noraidīts.

## Kādēļ DB testi nav izpildīti lokāli

Šajā izpildvidē nav Docker. PostgreSQL 16.15 binārais fails tika sagatavots izolētā mapē, taču process nedrīkst darboties kā root un vide neatļauj pārslēgties uz nepriviliģētu UID (`setresuid: Invalid argument`). Šis ierobežojums nav apiets. Nevienai PostgreSQL instancei nav pieslēgts, DB nav izveidota vai rakstīta. Docker/Windows skripts šajā vidē nav izpildīts; to pārbaudīs sagatavotā GitHub Actions darbplūsma vai Windows lokālais skrējiens.

## Droša Windows 11 pārbaude

Prasības: .NET SDK 10, Docker Desktop ar Linux konteineriem, PowerShell 5.1+; pirmajā reizē internets NuGet un oficiālā `postgres:16.15` attēla saņemšanai. Izpako ZIP jaunā mapē. No pakotnes saknes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\source\deploy\aethermu-europe\gr-postgresql\Test-GrandResetPostgreSql.ps1
```

Skripts veido tikai jaunu konteineru ar nejaušu nosaukumu un DB `aether_gr_test_<32 hex>`, nejaušu testa paroli, loopback adresē publicētu nejaušu portu un īslaicīgu datu failu sistēmu. Pirms pirmās rakstīšanas pārbauda `current_database()`. Testi papildus pārbauda DB nosaukuma/nonce sakritību, servera atgriezto identitāti, īpašu DB komentāru un sākotnēji tukšu datubāzi. `openmu`, `aether_ascension_test`, `postgres` un neskaidri nosaukumi tiek noraidīti pirms testa rakstīšanas.

Netiek lasīts vai lietots lietotāja ConnectionSettings.xml, OPENMU_TEST_POSTGRES vai produkcijas savienojums. Skripts nepieņem ārēju DB savienojuma virkni. Beigās tas noņem tikai paša izveidoto konteineru pēc pilnā ID un īpašumtiesību marķējuma pārbaudes; nekāda esošas DB dzēšanas komanda nav iekļauta. TRX paliek `source/runs/gr-<nonce>`.

Lokālais runner pieprasa visu septiņu īsto DB scenāriju PASS; izlaists vai trūkstošs tests ir kļūda. ZIP satur pilnu avota momentuzņēmumu un patch failus; serveris vai MU klients netiek startēts.

## Atlikušie riski un integrācijas slieksnis

1. Jāizpilda visi 7 PostgreSQL scenāriji. Līdz tam DB integrācija nav apstiprināta.
2. Vajadzīgs atsevišķs PostgreSQL procesa restartēšanas tests un īsts Windows spēles klienta tests, ieskaitot atslēgšanos pēc nenoteikta komita.
3. DB testi neaptver visas item opciju/kombināciju variācijas, tirgošanos vai citas sistēmas, kas ārpus GR vienlaicīgi maina to pašu raksturu.
4. Rindas bloķēšana serializē apmaiņas. Tā neaizstāj konta vienas aktīvas sesijas noteikumu un neievieš globālu optimistic-concurrency visiem pārējiem servera rakstītājiem. Patvaļīgs cits novecojis konteksts, kas neizmanto apmaiņas metodi, joprojām ir atsevišķs risks.
5. Nenoteikta komita gadījumā tiek lietota droša sesijas pārtraukšana un reload; šeit nav ieviests atsevišķs durable operation-ID žurnāls vai patvaļīgu ārēju atkārtojumu exactly-once API.
6. Ja atlasītajam raksturam ir jauni, vēl nesaglabāti statistikas ieraksti, apmaiņa tiek droši noraidīta; vispirms tiem jābūt persistētiem.
7. Integrēt tikai pēc PostgreSQL PASS un manuālas pārskatīšanas izolētā kopijā. Paralēlā Full Integration darba atzari nav mainīti.

## GitHub un atļaujas

OpenMU atzara izveide ar savienoto GitHub lietotni saņēma `403 Resource not accessible by integration`. Tāpēc tiešs OpenMU Draft PR nav izveidojams ar pašreizējām tiesībām. Pārnesams cumulative patch un PostgreSQL CI tiek sagatavots atsevišķā BalticAetherMU atzarā; tas neapvieno un nemaina Full Integration darbu.

Atsevišķa GitHub issue izveidi automātiskā pārbaude noraidīja kā ārpus lietotāja atļautā tvēruma. Issue nav izveidots, un noraidījums nav apiets. Tas nav šķērslis pieprasītā Draft PR sagatavošanai.

Nav merge, force push, produkcijas izvietošanas, reālu maksājumu vai izmaiņu lietotāja datorā. Esošā openmu DB nav aiztikta; -reinit un destruktīvas migrācijas nav lietotas.
