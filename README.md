# Dynamic Full Text Search

A library that provides dynamic Full Text Search and Regex support for PostgreSQL.

## Install 

```bash 
  dotnet add package DynamicFullTextSearchService --version 1.0.0
```

## Step-1
Create the necessary property for the table you will use.
#### Important: Make sure the property name is the same as in the example.
```csharp
public NpgsqlTsVector? search_vector { get; set; }
```


## Step-2
The trigger automatically populates the search_vector column when adding or updating data to a table.
```csharp
using (var context = new MyDbContext())
{
    var searchService = new DynamicFullTextSearchService<MyDbContext>(context);

    //Adds a Trigger to the table.
    //Trigger automatically populates the search_vector column when inserting or updating data in a table.
    await searchService.CreateTriggerForSearchVectorAsync(
        tableName: "YourTable", //The table name to which the trigger will be added.
        language: "Language", //The language to use in the search.
        columns: new[] { "Your Columns" } //Columns to use for search_vector.
    );
}
```

## Step-3
Adds a GIN index for the search_vector column to the table. This improves search performance.
```csharp
using (var context = new MyDbContext())
{
    var searchService = new DynamicFullTextSearchService<MyDbContext>(context);
    
    await searchService.CreateSearchVectorIndexAsync("YourTable");
}
```
## You only need to use Step-2 and Step-3 once.
## Step-4
After running Step-2 and Step-3 once, you are ready to use the Search method.
```csharp
using (var context = new MyDbContext())
{
    var searchService = new DynamicFullTextSearchService<MyDbContext>(context);

    var results = searchService.DynamicSearch<MyEntity>(
        searchKeyword: "example",
        language: "english",
        x => x.Title,
        x => x.Description
    ).ToList();
}
```

  
