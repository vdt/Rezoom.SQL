﻿namespace Rezoom.SQL.Mapping
open System
open System.Data
open System.Collections.Generic
open Rezoom
open Rezoom.SQL.Mapping.CodeGeneration

type CommandFragment =
    /// A name which should be localized to this command for batching.
    /// For example, if the command creates a temp table, the real name should be chosen dynamically
    /// so it doesn't break when the command is batched with others that create the same-named temp table.
    | LocalName of string
    /// Chunk of raw SQL text.
    | CommandText of string
    /// References parameter by index.
    | Parameter of int
    /// At least one unit of whitespace.
    | Whitespace
    /// Whitespace, preferably a line break.
    | LineBreak
    /// Increase indentation level for following line breaks.
    | Indent
    /// Decrease indentation level for following line breaks.
    | Outdent
    /// Converts a sequence of fragments *without parameters* to a string.
    static member Stringize(newline : string, indent : string, fragments : CommandFragment seq) =
        seq {
            let mutable indentation = ""
            let mutable pendingLine = false
            for fragment in fragments do
                let text =
                    match fragment with
                    | LocalName name -> Some name
                    | CommandText text -> Some text
                    | Whitespace -> Some " "
                    | Parameter i -> Some ("@P" + string i)
                    | LineBreak ->
                        pendingLine <- true
                        None
                    | Indent ->
                        indentation <- indentation + indent
                        None
                    | Outdent ->
                        indentation <- indentation.Substring(0, max 0 (indentation.Length - indent.Length))
                        None
                match text with
                | None -> ()
                | Some text ->
                    if pendingLine then
                        pendingLine <- false
                        yield newline
                        yield indentation
                    yield text
        } |> String.concat ""
    static member Stringize(fragments : CommandFragment seq) =
        CommandFragment.Stringize(" ", "", fragments)

[<AbstractClass>]
type ResultSetProcessor() =
    /// Start processing a result set.
    abstract member BeginResultSet : IDataReader -> unit
    /// Process a single row of the result set.
    abstract member ProcessRow : unit -> unit
    /// Obtain the result object after processing *all* result sets.
    abstract member ObjectGetResult : unit -> obj

[<AbstractClass>]
type ResultSetProcessor<'output>() =
    inherit ResultSetProcessor()
    abstract member GetResult : unit -> 'output
    override this.ObjectGetResult() = this.GetResult() |> box

type CommandData =
    {   ConnectionName : string
        Identity : string
        Fragments : CommandFragment IReadOnlyList
        DependencyMask : BitMask
        InvalidationMask : BitMask
        Cacheable : bool
        ResultSetCount : int option
    }

type CommandCategory = CommandCategory of connectionName : string

type CommandParameter =
    | ListParameter of DbType * Array
    | ScalarParameter of DbType * obj

[<AbstractClass>]
type Command(data : CommandData, parameters : CommandParameter IReadOnlyList) =
    let category = CommandCategory data.ConnectionName
    let cacheInfo =
        { new CacheInfo() with
            override __.Category = upcast category
            override __.Identity = upcast data.Identity
            override __.DependencyMask = data.DependencyMask
            override __.InvalidationMask = data.InvalidationMask
            override __.Cacheable = data.Cacheable
        }
    member __.ConnectionName = data.ConnectionName
    member __.CacheInfo = cacheInfo
    member __.Fragments = data.Fragments
    member __.Parameters = parameters
    /// The number of result sets this command will return, if it can be statically determined.
    member __.ResultSetCount = data.ResultSetCount

    abstract member ObjectResultSetProcessor : unit -> ResultSetProcessor

/// Represents multiple result sets as the output from a single command.
[<AbstractClass>]
type ResultSets() =
    abstract member AllResultSets : obj seq

type ResultSets<'a, 'b>(a : 'a, b : 'b) =
    inherit ResultSets()
    member __.ResultSet1 = a
    member __.ResultSet2 = b
    override __.AllResultSets =
        Seq.ofArray [| box a; box b |]

type ResultSets<'a, 'b, 'c>(a : 'a, b : 'b, c : 'c) =
    inherit ResultSets()
    member __.ResultSet1 = a
    member __.ResultSet2 = b
    member __.ResultSet3 = c
    override __.AllResultSets =
        Seq.ofArray [| box a; box b; box c |]

type ResultSets<'a, 'b, 'c, 'd>(a : 'a, b : 'b, c : 'c, d : 'd) =
    inherit ResultSets()
    member __.ResultSet1 = a
    member __.ResultSet2 = b
    member __.ResultSet3 = c
    member __.ResultSet4 = d
    override __.AllResultSets =
        Seq.ofArray [| box a; box b; box c; box d |]

/// A command which can be expected to produce `'output` when run.
[<AbstractClass>]
type Command<'output>(data, parameters) =
    inherit Command(data, parameters)
    abstract member WithConnectionName : connectionName : string -> Command<'output>
    abstract member ResultSetProcessor : unit -> ResultSetProcessor<'output>
    override this.ObjectResultSetProcessor() = upcast this.ResultSetProcessor()

type private ResultSetProcessor0<'a>() =
    inherit ResultSetProcessor<'a>()
    override __.BeginResultSet(_) = ()
    override __.ProcessRow() = ()
    override __.ObjectGetResult() = upcast Unchecked.defaultof<'a>
    override __.GetResult() = Unchecked.defaultof<'a>

type private Command0(data, parameters) =
    inherit Command<unit>(data, parameters)
    override __.WithConnectionName(connectionName) =
        upcast Command0({ data with ConnectionName = connectionName}, parameters)
    override __.ResultSetProcessor() = upcast ResultSetProcessor0<unit>()

type private ResultSetProcessor1<'a>() =
    inherit ResultSetProcessor<'a>()
    let reader = ReaderTemplate<'a>.Template().CreateReader()
    let mutable row = Unchecked.defaultof<Row>
    let result = lazy reader.ToEntity()
    override __.BeginResultSet(dataReader) =
        reader.ProcessColumns(DataReader.columnMap(dataReader))
        row <- DataReader.DataReaderRow(dataReader)
    override __.ProcessRow() =
        reader.Read(row)
    override __.GetResult() = result.Value

type private Command1<'a>(data, parameters) =
    inherit Command<'a>(data, parameters)
    override __.WithConnectionName(connectionName) =
        upcast Command1({ data with ConnectionName = connectionName}, parameters)
    override __.ResultSetProcessor() = upcast ResultSetProcessor1<'a>()

type private MultiResultSetProcessor(readers : EntityReader list) =
    let mutable row = Unchecked.defaultof<Row>
    let mutable readers = readers
    let mutable first = true
    member __.BeginResultSet(dataReader : IDataReader) =
        if not first then
            readers <- List.tail readers
        else
            first <- false
        (List.head readers).ProcessColumns(DataReader.columnMap(dataReader))
        row <- DataReader.DataReaderRow(dataReader)
    member __.ProcessRow() =
        (List.head readers).Read(row)

type private ResultSetProcessor2<'a, 'b>() =
    inherit ResultSetProcessor<ResultSets<'a, 'b>>()
    let aReader = ReaderTemplate<'a>.Template().CreateReader()
    let bReader = ReaderTemplate<'b>.Template().CreateReader()
    let proc = MultiResultSetProcessor([ aReader; bReader ])
    let result = lazy ResultSets<'a, 'b>(aReader.ToEntity(), bReader.ToEntity())
    override __.BeginResultSet(dataReader) = proc.BeginResultSet(dataReader)
    override __.ProcessRow() = proc.ProcessRow()
    override __.GetResult() = result.Value

type private Command2<'a, 'b>(data, parameters) =
    inherit Command<ResultSets<'a, 'b>>(data, parameters)
    override __.WithConnectionName(connectionName) =
        upcast Command2({ data with ConnectionName = connectionName}, parameters)
    override __.ResultSetProcessor() = upcast ResultSetProcessor2<'a, 'b>()

type private ResultSetProcessor3<'a, 'b, 'c>() =
    inherit ResultSetProcessor<ResultSets<'a, 'b, 'c>>()
    let aReader = ReaderTemplate<'a>.Template().CreateReader()
    let bReader = ReaderTemplate<'b>.Template().CreateReader()
    let cReader = ReaderTemplate<'c>.Template().CreateReader()
    let proc = MultiResultSetProcessor([ aReader; bReader; cReader ])
    let result = lazy ResultSets<'a, 'b, 'c>(aReader.ToEntity(), bReader.ToEntity(), cReader.ToEntity())
    override __.BeginResultSet(dataReader) = proc.BeginResultSet(dataReader)
    override __.ProcessRow() = proc.ProcessRow()
    override __.GetResult() = result.Value

type private Command3<'a, 'b, 'c>(data, parameters) =
    inherit Command<ResultSets<'a, 'b, 'c>>(data, parameters)
    override __.WithConnectionName(connectionName) =
        upcast Command3({ data with ConnectionName = connectionName}, parameters)
    override __.ResultSetProcessor() = upcast ResultSetProcessor3<'a, 'b, 'c>()

type private ResultSetProcessor4<'a, 'b, 'c, 'd>() =
    inherit ResultSetProcessor<ResultSets<'a, 'b, 'c, 'd>>()
    let aReader = ReaderTemplate<'a>.Template().CreateReader()
    let bReader = ReaderTemplate<'b>.Template().CreateReader()
    let cReader = ReaderTemplate<'c>.Template().CreateReader()
    let dReader = ReaderTemplate<'d>.Template().CreateReader()
    let proc = MultiResultSetProcessor([ aReader; bReader; cReader; dReader ])
    let result =
        lazy ResultSets<'a, 'b, 'c, 'd>
            (aReader.ToEntity(), bReader.ToEntity(), cReader.ToEntity(), dReader.ToEntity())
    override __.BeginResultSet(dataReader) = proc.BeginResultSet(dataReader)
    override __.ProcessRow() = proc.ProcessRow()
    override __.GetResult() = result.Value

type private Command4<'a, 'b, 'c, 'd>(data, parameters) =
    inherit Command<ResultSets<'a, 'b, 'c, 'd>>(data, parameters)
    override __.WithConnectionName(connectionName) =
        upcast Command4({ data with ConnectionName = connectionName}, parameters)
    override __.ResultSetProcessor() = upcast ResultSetProcessor4<'a, 'b, 'c, 'd>()

type CommandConstructor =
    static member Command0(data, parameters) =
        Command0(data, parameters) :> _ Command
    static member Command1<'a>(data, parameters) =
        Command1<'a>(data, parameters) :> _ Command
    static member Command2<'a, 'b>(data, parameters) =
        Command2<'a, 'b>(data, parameters) :> _ Command
    static member Command3<'a, 'b, 'c>(data, parameters) =
        Command3<'a, 'b, 'c>(data, parameters) :> _ Command
    static member Command4<'a, 'b, 'c, 'd>(data, parameters) =
        Command4<'a, 'b, 'c, 'd>(data, parameters) :> _ Command