# Format overview

> Source: https://learn.microsoft.com/en-us/dotnet/standard/metadata-and-self-describing-components

### Portable executable (PE)
.dll má reálně tzv. PE formát, který udává kind of potřebný kontext k spuštění daného kódu/poskytnutí ho do jiného programu

V PE formátu existuje flag, který udává jestli se jedná o .dll (je to lepší způsob jak hledat .PE soubory než skrz .dll příponu)

PE header má v sobě třeba adressu entrypointu svého kódu

## Metadata in .dll

V .dll je spousta věcí a mezi hlavními věci jsou metadata obsahující definované typy a jejich signatury (Typ že má nějaké atributy, metody a ty jaké mají signatury). Pak zvlášť se tam nachází samotný CIL kód, kde třeba method handler má referenci právě na nějaký takový kód.

Metadata obsahují následující informace:

1. Description of the assembly.  
 Identity (name, version, culture, public key).  
The types that are exported.  
Other assemblies that this assembly depends on.  
Security permissions needed to run.

2. Description of types.  
Name, visibility, base class, and interfaces implemented.  
Members (methods, fields, properties, events, nested types).

3. Attributes.  
Additional descriptive elements that modify types and members.

**Vysvětlivky**  
Culture assembly vysvětluje pro jakou culture je dělaná. samotný kód je většinou culture-neutral. Ale třeba cs-CZ/MyApp.resources.dll bude asi mít cs-CZ culture.

Public key umožňuje porovna hash assembly s přiložených hashem, který byl ještě zašifrován private key. Tady pozor když budu dělat novou assembly tak buď musím znát ten private key, nebo neposkytnout ve výstpupu ten zašifrovaný hash (jde dát na výstupu null)

Ostatní assembly na kterých to závisí jsou odkázané pomocí jejich identity (to v assembly description)

### Struktura metadat
Většina věcí je v tabulkách (tabulka pro třídy, tabulka pro metody, tabulka pro fields, ...)

Ale existují ještě 4 haldy (heaps):
- string (Zde jsou názvy všech typů a metod - z metadat se pouze referencuje offset na této haldě)
- blob (obsahuje složitější objekty na které se odkazuje z tabulek, třeba signatury)
- user string
- GUID

**Metadata Tokens** - jsou to basically pointers z CIL kódy na tabulky v metadatech (mají 4 byty, první identifikuje tabulku a spodní 3 identifikují řádek v té tabulce)

V tabulce pro třeba metody se většinou nachází tzv. Relative Virtual Address (RVA), což je offset od začátku PE souboru, který udává kde se nachází CIL kód pro danou metodu.