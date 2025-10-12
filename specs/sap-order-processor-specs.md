# SPECIFICHE SVILUPPO SERVIZIO SAP BUSINESS ONE - COPIA FILE DA DISTINTA BASE

## CONTESTO

Sviluppare un'applicazione .NET (C#) che si integri con SAP Business One tramite Service Layer per processare ordini clienti, estrarre componenti dalle distinte base e copiare file da cartelle master a cartelle di destinazione specifiche per ordine.

## ARCHITETTURA RICHIESTA

- **.NET 8** o superiore
- Architettura **pulita e stratificata**: Models, Services, Controllers/Handlers
- **Dependency Injection** per tutti i servizi
- Pattern **Repository** per accesso dati (se necessario)
- **Async/Await** per tutte le operazioni I/O
- Configurazione tramite **appsettings.json**
- Logging strutturato con **Serilog** o ILogger

## STRUTTURA PROGETTO

```
SapOrderFileProcessor/
├── Models/
│   ├── SapConfiguration.cs
│   ├── SalesOrder.cs
│   ├── OrderItem.cs
│   ├── BillOfMaterial.cs
│   ├── BomComponent.cs
│   └── ProcessingResult.cs
├── Services/
│   ├── ISapServiceLayerService.cs
│   ├── SapServiceLayerService.cs
│   ├── IFileManagementService.cs
│   ├── FileManagementService.cs
│   ├── IOrderProcessingService.cs
│   └── OrderProcessingService.cs
├── appsettings.json
└── Program.cs
```

## CONFIGURAZIONE (appsettings.json)

Deve contenere:

- **ServiceLayerUrl**: URL del Service Layer SAP B1 (es: `https://server:50000/b1s/v1`)
- **CompanyDB**: Nome database company
- **UserName**: Username SAP B1
- **Password**: Password SAP B1
- **MasterFolder**: Percorso cartella master contenente i file dei componenti (es: `C:\SAP_Files\Master`)
- **DestinationBaseFolder**: Percorso base per cartelle ordini (es: `C:\SAP_Files\Orders`)
- **FileSearchPattern**: Pattern per cercare file (es: `{ItemCode}_*.pdf` o `{ItemCode}.*`)
- **LogLevel**: Configurazione logging

### Esempio appsettings.json

```json
{
  "SapConfiguration": {
    "ServiceLayerUrl": "https://server:50000/b1s/v1",
    "CompanyDB": "SBODemoIT",
    "UserName": "manager",
    "Password": "password",
    "MasterFolder": "C:\\SAP_Files\\Master",
    "DestinationBaseFolder": "C:\\SAP_Files\\Orders",
    "FileSearchPattern": "{ItemCode}*.*"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

## MODELLI DATI

### SalesOrder
- DocEntry (int)
- DocNum (int)
- CardCode (string)
- CardName (string)
- Items (List&lt;OrderItem&gt;)

### OrderItem
- ItemCode (string)
- ItemName (string)
- Quantity (decimal)
- LineNum (int)
- HasBom (bool)

### BillOfMaterial
- ParentItemCode (string)
- Components (List&lt;BomComponent&gt;)

### BomComponent
- ItemCode (string)
- ItemName (string)
- Quantity (decimal)
- LineNum (int)

### ProcessingResult
- Success (bool)
- OrderDocNum (int)
- ProcessedComponents (int)
- CopiedFiles (int)
- Errors (List&lt;string&gt;)
- WarningMessages (List&lt;string&gt;)
- ExecutionTimeMs (long)

## SERVIZIO SAP SERVICE LAYER

### Interfaccia ISapServiceLayerService

```csharp
public interface ISapServiceLayerService
{
    Task<string> LoginAsync();
    Task<SalesOrder> GetSalesOrderAsync(int docEntry, string sessionId);
    Task<BillOfMaterial> GetBillOfMaterialAsync(string itemCode, string sessionId);
    Task LogoutAsync(string sessionId);
}
```

### Implementazione - Dettagli Tecnici

- Usare **HttpClient** con dependency injection
- Gestire cookie **B1SESSION** nelle chiamate
- **Endpoint SAP**:
  - Login: `POST /Login`
  - Orders: `GET /Orders(docEntry)`
  - Bill of Materials: `GET /ProductTrees?$filter=ItemCode eq '{itemCode}'`
- Gestire errori HTTP (401, 404, 500)
- Timeout configurabile (es. 30 secondi)
- **Retry policy** per errori transienti (usare Polly)

### Headers Richiesti

```
Content-Type: application/json
Cookie: B1SESSION={sessionId}
```

### Gestione Autenticazione

1. POST a `/Login` con body:
```json
{
  "CompanyDB": "SBODemoIT",
  "UserName": "manager",
  "Password": "password"
}
```

2. Estrarre SessionId dalla risposta
3. Includere SessionId in tutte le chiamate successive
4. Logout al termine con `POST /Logout`

## SERVIZIO FILE MANAGEMENT

### Interfaccia IFileManagementService

```csharp
public interface IFileManagementService
{
    Task<string> CreateOrderFolderAsync(int docNum);
    Task<List<string>> FindComponentFilesAsync(string itemCode);
    Task<List<string>> CopyFilesAsync(List<string> sourceFiles, string destinationFolder);
    Task<bool> ValidateMasterFolderAsync();
}
```

### Logica Implementazione

#### FindComponentFilesAsync
- Cerca file usando pattern configurabile: `{ItemCode}.*` o `{ItemCode}_*.*`
- Supporta wildcards
- Case-insensitive su Windows
- Restituisce lista percorsi completi

#### CreateOrderFolderAsync
- Crea cartella con nome: `Order_{DocNum}`
- Verifica esistenza prima di creare
- Gestisce path esistenti senza errore
- Log operazione

#### CopyFilesAsync
- Copia ogni file dalla lista nella destinazione
- Gestisci conflitti nomi file (aggiungi suffisso numerico: `file_1.pdf`, `file_2.pdf`)
- Preserva estensione originale
- Log ogni copia con percorsi completi
- Restituisce lista file copiati

#### Gestione Eccezioni
- `UnauthorizedAccessException`: Permessi insufficienti
- `IOException`: File in uso, spazio disco
- `DirectoryNotFoundException`: Path non valido
- Log errore con dettagli e continua con file successivo

## SERVIZIO ORCHESTRAZIONE (OrderProcessingService)

### Interfaccia IOrderProcessingService

```csharp
public interface IOrderProcessingService
{
    Task<ProcessingResult> ProcessOrderAsync(int docEntry, CancellationToken cancellationToken = default);
}
```

### Flusso Logica Completo

```
1. Validazione parametri input
2. Login a Service Layer SAP
   └─ Se fallisce: return ProcessingResult con errore
   
3. Recupera ordine cliente tramite DocEntry
   └─ Se non trovato: log errore e return
   
4. Crea cartella destinazione Order_{DocNum}
   
5. Per ogni articolo dell'ordine:
   ├─ Verifica se ha distinta base
   │  ├─ SE SÌ:
   │  │  ├─ Recupera componenti dalla distinta
   │  │  └─ Per ogni componente:
   │  │     ├─ Cerca file nella MasterFolder
   │  │     ├─ Se trovati: copia nella cartella ordine
   │  │     └─ Se non trovati: log warning
   │  └─ SE NO:
   │     ├─ Considera l'articolo stesso come "componente"
   │     ├─ Cerca file nella MasterFolder
   │     └─ Copia se trovato
   
6. Crea ProcessingResult con statistiche
7. Logout da Service Layer (anche in caso di errore)
8. Return ProcessingResult
```

### Gestione Errori

- **Continua processamento** anche se alcuni file mancano
- Colleziona tutti gli errori e warnings in ProcessingResult
- Non bloccare su singolo componente mancante
- Log strutturato con severity appropriata (Error, Warning, Info)
- Try-catch su ogni operazione critica
- Finally block per garantire Logout

### Esempio Pseudo-codice

```csharp
public async Task<ProcessingResult> ProcessOrderAsync(int docEntry, CancellationToken ct)
{
    var result = new ProcessingResult();
    string sessionId = null;
    
    try
    {
        // Login
        sessionId = await _sapService.LoginAsync();
        
        // Get Order
        var order = await _sapService.GetSalesOrderAsync(docEntry, sessionId);
        result.OrderDocNum = order.DocNum;
        
        // Create folder
        var orderFolder = await _fileService.CreateOrderFolderAsync(order.DocNum);
        
        // Process items
        foreach (var item in order.Items)
        {
            var bom = await _sapService.GetBillOfMaterialAsync(item.ItemCode, sessionId);
            
            if (bom.Components.Any())
            {
                // Ha distinta base
                foreach (var component in bom.Components)
                {
                    await ProcessComponent(component.ItemCode, orderFolder, result);
                }
            }
            else
            {
                // Nessuna distinta, processa articolo stesso
                await ProcessComponent(item.ItemCode, orderFolder, result);
            }
        }
        
        result.Success = true;
    }
    catch (Exception ex)
    {
        result.Success = false;
        result.Errors.Add(ex.Message);
    }
    finally
    {
        if (sessionId != null)
            await _sapService.LogoutAsync(sessionId);
    }
    
    return result;
}
```

## LOGGING

### Eventi da Loggare

**Information Level**:
- Inizio processamento ordine (DocEntry)
- Login SAP riuscito
- Ordine recuperato (DocNum, Cliente)
- Cartella ordine creata
- File trovato per componente
- File copiato con successo
- Completamento processamento con statistiche

**Warning Level**:
- Componente senza file nella master folder
- File già esistente nella destinazione (sovrascritto/rinominato)
- Articolo senza distinta base

**Error Level**:
- Errore login SAP
- Ordine non trovato
- Errore recupero distinta base
- Errore creazione cartella
- Errore copia file
- Eccezioni non gestite

### Formato Log Suggerito

```
[2025-09-30 14:30:15] INFO - Inizio processamento ordine DocEntry: 12345
[2025-09-30 14:30:16] INFO - Login SAP riuscito. SessionId: xxx
[2025-09-30 14:30:17] INFO - Ordine recuperato: DocNum=5001, Cliente=ACME Corp
[2025-09-30 14:30:18] INFO - Cartella creata: C:\SAP_Files\Orders\Order_5001
[2025-09-30 14:30:19] INFO - Articolo A001: trovati 3 componenti in distinta base
[2025-09-30 14:30:20] INFO - Componente C001: trovati 2 file
[2025-09-30 14:30:20] INFO - File copiato: C001_Drawing.pdf -> Order_5001\C001_Drawing.pdf
[2025-09-30 14:30:21] WARN - Componente C002: nessun file trovato nella master folder
[2025-09-30 14:30:25] INFO - Processamento completato: 5 articoli, 12 componenti, 18 file copiati
```

## GESTIONE SESSIONI SAP

### Best Practices

1. **Login** all'inizio del processo
2. **Riuso SessionId** per tutte le chiamate successive
3. **Logout** al termine (anche in caso di errore - usare finally)
4. **Gestione timeout**: Service Layer ha timeout default ~30 minuti
5. **Refresh token**: se processo lungo, implementare keep-alive

### Esempio Gestione Sessione

```csharp
string sessionId = null;
try
{
    sessionId = await LoginAsync();
    // ... operazioni ...
}
finally
{
    if (sessionId != null)
    {
        try
        {
            await LogoutAsync(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore durante logout");
        }
    }
}
```

## REQUISITI NON FUNZIONALI

### Performance
- Operazioni parallele dove possibile (es. copia file multipli con `Task.WhenAll`)
- Evitare chiamate sincrone bloccanti
- Cache per distinte base già recuperate (opzionale)
- Limite timeout su operazioni rete (30s)

### Resilienza
- Retry automatico per errori rete con backoff esponenziale (Polly)
- Graceful degradation: continua anche se alcuni file mancano
- Circuit breaker per Service Layer (opzionale)

### Sicurezza
- Password **MAI** in chiaro nei log
- Considerare encryption per password in appsettings (User Secrets, Azure Key Vault)
- Validazione input (DocEntry > 0)
- Sanitizzazione path file

### Testabilità
- Tutte le dipendenze iniettate tramite interfacce
- Metodi piccoli e focalizzati (Single Responsibility)
- Mock per Service Layer e File System nei test

### Manutenibilità
- Codice pulito con naming conventions C#
- Commenti XML per API pubbliche
- Gestione eccezioni consistente
- Separazione responsabilità (SOLID)

## GESTIONE CASISTICHE SPECIALI

### 1. Articoli senza distinta base
**Comportamento**: Trattare l'articolo stesso come "componente" e cercare i suoi file nella master folder

### 2. Componenti con distinte multilivello
**Decisione necessaria**: 
- **Opzione A**: Solo primo livello (più semplice)
- **Opzione B**: Ricorsivo multi-livello (più completo ma complesso)

**Raccomandazione**: Iniziare con Opzione A, aggiungere B se necessario

### 3. File duplicati nella master folder
**Comportamento**: Copiare tutti i file che matchano il pattern

Esempio:
```
Master/
  C001.pdf
  C001_Drawing.pdf
  C001_Specs.pdf
```
Tutti e tre vengono copiati

### 4. Nomi file con caratteri speciali
**Gestione**: Sanitizzare caratteri non validi per Windows (`< > : " / \ | ? *`)

### 5. Ordini già processati
**Opzioni**:
- **A**: Sovrascrivere cartella esistente
- **B**: Saltare se cartella esiste
- **C**: Creare cartella con suffisso (Order_5001_1, Order_5001_2)

**Raccomandazione**: Opzione A con log warning

### 6. Master folder su rete
**Gestione**:
- Verificare accessibilità all'inizio
- Gestire disconnessioni temporanee con retry
- Timeout specifico per operazioni di rete

## OUTPUT DESIDERATO

### Console/Log Output

```
========================================
PROCESSAMENTO ORDINE - RIEPILOGO
========================================
Ordine: 5001 (DocEntry: 12345)
Cliente: ACME Corporation
Data elaborazione: 2025-09-30 14:30:25

Articoli processati: 5
Componenti trovati: 12
File copiati: 18

Warnings: 2
- Componente C002: nessun file trovato
- Componente C015: nessun file trovato

Errori: 0

Cartella destinazione: C:\SAP_Files\Orders\Order_5001
Tempo esecuzione: 8.3 secondi
========================================
```

### ProcessingResult (JSON)

```json
{
  "success": true,
  "orderDocNum": 5001,
  "processedComponents": 12,
  "copiedFiles": 18,
  "errors": [],
  "warningMessages": [
    "Componente C002: nessun file trovato",
    "Componente C015: nessun file trovato"
  ],
  "executionTimeMs": 8342
}
```

## EXTRA (FUNZIONALITÀ OPZIONALI)

### 1. API REST
Esporre endpoint per invocare il processo:
```
POST /api/orders/process
Body: { "docEntry": 12345 }
Response: ProcessingResult
```

### 2. Database Storico
Tabella `ProcessingHistory`:
- Id, DocEntry, DocNum, ProcessedDate, Success, ComponentsCount, FilesCount, Errors

### 3. Notifiche Email
Inviare email su:
- Completamento con successo
- Errori critici
- Warning se molti file mancanti

### 4. Dashboard Monitoraggio
- Interfaccia web per visualizzare stato processamenti
- Grafici: ordini/giorno, success rate, file copiati

### 5. Configurazione Pattern per Tipo
```json
"FilePatterns": {
  "Default": "{ItemCode}*.*",
  "Drawings": "{ItemCode}_DWG.*",
  "Specs": "{ItemCode}_SPEC.*"
}
```

### 6. Elaborazione Batch
Processare lista di DocEntry in parallelo:
```csharp
Task<List<ProcessingResult>> ProcessOrdersBatchAsync(List<int> docEntries)
```

## PUNTO DI INGRESSO

### Console Application

```csharp
static async Task Main(string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine("Uso: SapOrderFileProcessor <DocEntry>");
        return;
    }
    
    int docEntry = int.Parse(args[0]);
    
    // Setup DI, logging, config
    var serviceProvider = ConfigureServices();
    
    var processor = serviceProvider.GetRequiredService<IOrderProcessingService>();
    var result = await processor.ProcessOrderAsync(docEntry);
    
    Console.WriteLine(result.Success ? "Successo" : "Fallito");
}
```

### API Endpoint

```csharp
[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderProcessingService _processor;
    
    [HttpPost("process")]
    public async Task<ActionResult<ProcessingResult>> ProcessOrder([FromBody] ProcessOrderRequest request)
    {
        var result = await _processor.ProcessOrderAsync(request.DocEntry);
        return Ok(result);
    }
}
```

## DIPENDENZE NUGET CONSIGLIATE

```xml
<PackageReference Include="Microsoft.Extensions.Configuration" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging" Version="8.0.0" />
<PackageReference Include="Microsoft.Extensions.Http" Version="8.0.0" />
<PackageReference Include="Serilog.Extensions.Logging" Version="8.0.0" />
<PackageReference Include="Serilog.Sinks.Console" Version="5.0.0" />
<PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
<PackageReference Include="Polly" Version="8.2.0" />
<PackageReference Include="System.Text.Json" Version="8.0.0" />
```

## CHECKLIST IMPLEMENTAZIONE

- [ ] Creare struttura progetto
- [ ] Implementare modelli dati
- [ ] Configurare appsettings.json
- [ ] Implementare SapServiceLayerService (login, get order, get BOM)
- [ ] Implementare FileManagementService (find, copy files)
- [ ] Implementare OrderProcessingService (orchestrazione)
- [ ] Configurare Dependency Injection
- [ ] Implementare logging strutturato
- [ ] Gestione errori e retry policy
- [ ] Testing con ordini reali SAP
- [ ] Documentazione API
- [ ] Deployment e configurazione

---

## NOTE FINALI

Questo documento fornisce tutte le specifiche necessarie per sviluppare un'applicazione enterprise-grade, ben strutturata, testabile e manutenibile per l'integrazione con SAP Business One.

**Priorità implementazione**:
1. Core functionality (SAP + File services)
2. Orchestrazione e gestione errori
3. Logging completo
4. Funzionalità opzionali
