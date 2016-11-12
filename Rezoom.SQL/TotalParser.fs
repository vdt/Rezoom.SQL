﻿// Parses all AST statements.

module private Rezoom.SQL.VendorStatementParser
open System
open System.Collections.Generic
open System.Globalization
open FParsec
open FParsec.Pipes
open FParsec.Pipes.Precedence
open Rezoom.SQL.Parser

// Vendor statements allow embedding raw SQL written for a specific backend.
// Sometimes the set of SQL we can typecheck is insufficient.
// In their simplest form, vendor statements look like:
// VENDOR TSQL {
//     -- literally any text could go in here
//     -- as an example I'm using T-SQL's delete/join syntax which is not supported by the typechecked language.
//     delete f from Foos f
//     join Bars b on b.FooId = f.Id
//     and f.Name like {@name}
// }

// Notice that the vendor statement includes the name of the backend it was written for.
// This is intended to ease transitions to other backends, because when you change the targeted
// backend in your config file, the compiler will shriek and point to all the places you're using
// vendor-specific SQL for the old backend.

// The delimiter can be any sequence of punctuation, in order to permit vendor statements
// that contain funky characters. For example, the above could also be written like:
// VENDOR TSQL {<[
//     delete f from Foos f
//     join Bars b on b.FooId = f.Id
//     and f.Name like {<[@name]>}
// ]>}

// Notice:
// 1. The closing delimiter is a "flipped" version of the opening delimiter, which is a string reverse
//    with some characters also switched out for their "inverse", e.g. [ and ].
// 2. The same delimiter pairs are used to wrap parameters used within the vendor statement.
//    We must separate parameters from the raw SQL text since when batching SQL statements, the parameter
//    names are generated dynamically at runtime.

// By default, since we can't statically determine anything about vendor statements,
// they have some unfortunate properties.
// 1. They are considered to invalidate the cache for all tables. (we assume the worst)
// 2. The parameters used with them get no type constraints added, so if they are not used
//    in another statement, they will all have `obj` type.
// 3. They are asssumed to have no result sets of interest.

// In order to avoid this, you should specify a second set of statements in the typechecked SQL dialect.
// These will never execute, and therefore need not match the actual semantics of the vendor statements, but
// the typechecker will use them to assume facts about the parameters and cache effects of the vendor statements.
// You do this with the IMAGINE clause. For example:
// VENDOR TSQL {
//     select Name, count(*) over (partition by Name) as Count from Users group by Name
// } IMAGINE {
//     -- inform the typechecker that we depend on the Users table and have a result set
//     -- of type (Name string, Count int)
//     select Name, 1 as Count from Users
// }

let private definingDelimiter =
    many1Satisfy Char.IsPunctuation
    
let private flipChar =
    function
    | '[' -> ']'
    | ']' -> '['
    | '{' -> '}'
    | '}' -> '{'
    | '(' -> ')'
    | ')' -> '('
    | '<' -> '>'
    | '>' -> '<'
    | '/' -> '\\'
    | '\\' -> '/'
    | c -> c

let private flipDelimiter (delim : string) =
    delim.ToCharArray()
    |> Array.rev
    |> Array.map flipChar
    |> String

let private vendorStmtStart =
    %% ci "VENDOR"
    -- ws1
    -- +.name
    -- ws1
    -- +.definingDelimiter
    -|> fun vendorName delim -> vendorName, delim

type private Delimiter =
    | OpenDelimiter
    | CloseDelimiter

let private vendorFragments openDelim =
    let closeDelim = flipDelimiter openDelim
    if closeDelim = openDelim then
        fail (sprintf "Opening and closing delimiters are the same (%s)" closeDelim)
    else
        let openDelim = pstring openDelim >>% OpenDelimiter
        let closeDelim = pstring closeDelim >>% CloseDelimiter
        let delim = openDelim <|> closeDelim
        let exprWithClose = expr .>> ws .>> closeDelim
        let onExpr str e next =
            VendorRaw str
            :: VendorEmbeddedExpr e
            :: next
        let self, selfRef = createParserForwardedToRef()
        let onOpen str =
            pipe2 exprWithClose self (onExpr str)
        let onClose str =
            preturn [ VendorRaw str ]
        let onEither (str, delim) =
            match delim with
            | OpenDelimiter -> onOpen str
            | CloseDelimiter -> onClose str
        selfRef :=
            manyCharsTillApply anyChar delim
                (fun str delim -> str, delim)
            >>= onEither
        self |>> ResizeArray

let private vendorStmt =
    vendorStmtStart
    >>= fun (vendorName, delim) ->
        vendorFragments delim
        |>> fun frags ->
            {   VendorName = vendorName
                Fragments = frags
                ImaginaryStmts = None
            } |> VendorStmt