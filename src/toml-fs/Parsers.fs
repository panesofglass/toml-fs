﻿#nowarn "62"
namespace TomlFs

open System
open System.Collections.Generic
open FParsec
open FParsec.Primitives
open TomlFs.AST
open System.Text

module Parsers =

    type UserState = unit    
    type Parser<'t> = Parser<'t,UserState>

    (*|---------------------------------------------|*)
    (*| Whitespace/Comment/LineEnd AKA Skip Parsers |*)
    (*|---------------------------------------------|*)

    /// toml approved whitespace is ' ' or '\t'
    let skip_tspcs      : Parser<_>  = skipManySatisfy (isAnyOf [' ';'\t'])
    let tskipRestOfLine : Parser<_>  = choice [ skipChar '#' >>. skipRestOfLine  true  
                                                skipRestOfLine  true ]


    (*|---------------------|*)
    (*| Punctuation Parsers |*)
    (*|---------------------|*)


    let ``.``   : Parser<_> = pchar   '.'  .>> skip_tspcs 
    let ``,``   : Parser<_> = pchar   ','  .>> skip_tspcs   
    let ``[``   : Parser<_> = pchar   '['  .>> skip_tspcs              
    let ``]``   : Parser<_> = pchar   ']'  .>> skip_tspcs   
    let ``{``   : Parser<_> = pchar   '{'  .>> skip_tspcs   
    let ``}``   : Parser<_> = pchar   '}'  .>> skip_tspcs    
    let ``[[``  : Parser<_> = pstring "[[" .>> skip_tspcs  
    let ``]]``  : Parser<_> = pstring "]]" .>> skip_tspcs  
    let ``"``   : Parser<_> = pchar   '"'
    let ``'``   : Parser<_> = pchar   '\''
    let ``"""`` : Parser<_> = pstring "\"\"\""
    let ``'''`` : Parser<_> = pstring "\'\'\'"


    let inline isEscChar c = c = '\\'  
    let inline isAnyChar _ = true  
    // parsers for string bounds that won't be fooled by escaped quotes
    let prevCharNot = previousCharSatisfiesNot
    let prevCharIs  = previousCharSatisfies 
    let ``|"|``   : Parser<_> = prevCharNot isEscChar >>. pchar '"'
    let ``|'|``   : Parser<_> = prevCharNot isEscChar >>. pchar '\''
    let ``|"""|`` : Parser<_> = prevCharNot isEscChar >>. pstring "\"\"\""
    let ``|'''|`` : Parser<_> = prevCharNot isEscChar >>. pstring "\'\'\'"
    let skipEqs   : Parser<_> = skipChar '=' >>. skip_tspcs

    
    (*|----------------|*)
    (*| String Parsers |*)
    (*|----------------|*)


    let (|EscChar|_|) ch (twoChar:TwoChars) = if TwoChars('\\',ch) = twoChar then Some EscChar else None

    // unicode control chars
    let isCtrlChar = isAnyOf ([for hex in 0x00..0x1f -> char hex]@['?'])

    let ctrlChar  : Parser<_> = 
        let ctrlLabel = "Unicode Control Characters [0x00-0x1f] and `?` must be preceded by a `\` in basic TOML strings"
        (prevCharIs isEscChar >>. satisfyL isCtrlChar ctrlLabel) <??> ctrlLabel
    
    let ``\``          : Parser<_> = pchar '\\' 
    let ``\b``         : Parser<_> = ``\`` >>. pchar 'b'  >>% '\u0008'
    let ``\t``         : Parser<_> = ``\`` >>. pchar 't'  >>% '\u0009'
    let ``\n``         : Parser<_> = ``\`` >>. pchar 'n'  >>% '\u000A'
    let ``\f``         : Parser<_> = ``\`` >>. pchar 'f'  >>% '\u000C'
    let ``\r``         : Parser<_> = ``\`` >>. pchar 'r'  >>% '\u000D'
    let ``\"``         : Parser<_> = ``\`` >>. pchar '"'  >>% '\u0022'
    let ``\\``         : Parser<_> = ``\`` >>. pchar '\\' >>% '\u005C'
    let ``\uXXXX``     : Parser<_> = ``\`` >>. pchar 'u'  >>. anyString 4 |>> (sprintf "\u%s">> Char.Parse)
    let ``\UXXXXXXXX`` : Parser<_> = ``\`` >>. pchar 'U'  >>. anyString 8 |>> (sprintf "\U%s">> Char.Parse)


    let rec private string_char flag startIndex (stream: CharStream<_>) =
        match stream.Peek () with
        | '"' when flag -> ``"`` stream // `"` doesn't need to be escaped in a multi-line string
        | '\\' -> 
            match stream.Peek2 () with
            | EscChar 'b'  -> ``\b``         stream
            | EscChar 't'  -> ``\t``         stream
            | EscChar 'n'  -> ``\n``         stream
            | EscChar 'f'  -> ``\f``         stream
            | EscChar 'r'  -> ``\r``         stream
            | EscChar '"'  -> ``\"``         stream
            | EscChar '\\' -> ``\\``         stream
            | EscChar 'u'  -> ``\uXXXX``     stream
            | EscChar 'U'  -> ``\UXXXXXXXX`` stream
            | EscChar '\n' when flag -> 
                    // For writing long strings without introducing extraneous whitespace, end a line with a \. 
                    // The \ will be trimmed along with all whitespace (including newlines) up to the next 
                    // non-whitespace character or closing delimiter.
                    stream.Skip ()                    
                    stream.SkipUnicodeNewline ()     |> ignore
                    stream.SkipUnicodeWhitespace ()  |> ignore
                    string_char true startIndex stream
            | twoChar      -> 
                Reply (Error, 
                    sprintf "'\\%c' is not a valid TOML escape character\n\
                            Only the following esc sequences are accepted :\n\
                            \\b \\t \\n \\f \\r \\\" \\\\ \\uXXXX \\UXXXXXXXX" 
                            twoChar.Char1 |> messageError)
        | '\n' -> 
            if flag && stream.Index = (startIndex+3L) then 
                stream.SkipNewline () |> ignore 
                string_char true startIndex stream
            elif flag then unicodeNewline stream else            
            Reply( Error,  "parsed a linebreak in a basic string. Linebreaks are not valid in \
                            Toml basic strings"|> messageError)
        | c when isCtrlChar c -> ctrlChar stream
        | _ -> satisfy (isNoneOf['"']) stream


    let basic_string : Parser<_> = 
        between ``|"|``   ``|"|``   (many1Chars (string_char false 0L))


    let multi_string : Parser<_> = 
        let inline psr (stream:CharStream<_>) =
            let multi_string_char = string_char true stream.Index
            (between ``|"""|`` ``|"""|`` (many1Chars multi_string_char)) stream
        psr


    let literal_string : Parser<_> = 
        between ``|'|``   ``|'|`` (manySatisfy (isNoneOf['\'';'\n']))


    let multi_literal_string : Parser<_> = 
        let mlit_char = choice [attempt(unicodeNewline>>.manySatisfy isAnyChar);manySatisfy isAnyChar]
        between ``|'''|`` ``|'''|`` mlit_char
    

    let pString_toml : Parser<_> =
        choice [basic_string; multi_string; literal_string; multi_literal_string] .>> skip_tspcs


    (*|----------------------|*)
    (*| Simple Value Parsers |*)
    (*|----------------------|*)


    let pInt64_toml : Parser<_> = 
        choice[ 
            pstring "0";
            followedByL (satisfy ((<>)'0')) "TOML ints cannot begin with leading 0s"
            >>. many1Chars (prevCharIs isDigit >>. skipChar '_' >>. digit <|> digit)
            .>> notFollowedByL ``.`` "TOML ints cannot contain `.`"]
        |>> int64 


    let isFloatChar = isDigit|?|isAnyOf['e';'E';'+';'-';'.']

    let pFloat_toml : Parser<_> = 
        let floatChar = satisfy isFloatChar
        choice[
            pstring "0.0";
            followedByL (satisfy ((<>)'0')) "TOML floats cannot begin with leading 0s"  
            >>. many1Chars (skipChar '_' >>. floatChar <|> floatChar)]
        |>> float


    let private toDateTime str =
        let mutable dt = Unchecked.defaultof<DateTime>
        match DateTime.TryParse (str, &dt) with
        | false -> failwithf "failed parsing into DateTime - %s" str
        | true  -> dt

    let isDateChar = isDigit|?|isAnyOf['T';':';'.';'-';'Z']

    let pDateTime_toml : Parser<_> = manySatisfy isDateChar |>> toDateTime
    let pBool_toml     : Parser<_> = (pstring "false" >>% false) <|> (pstring "true" >>% true)

    let toml_int      = pInt64_toml     |>> Value.Int
    let toml_float    = pFloat_toml     |>> Value.Float
    let toml_datetime = pDateTime_toml  |>> Value.DateTime
    let toml_bool     = pBool_toml      |>> Value.Bool
    let toml_string   = pString_toml    |>> Value.String


    (*|-------------|*)
    (*| Key Parsers |*)
    (*|-------------|*)


    let isKeyStart = isDigit|?|isLetter|?|isAnyOf['"';'\'']

    // key formats
    let pBareKey          : Parser<_> = many1Satisfy (isDigit|?|isLetter|?|isAnyOf['_';'-']) 
    let pQuoteKey         : Parser<_> = between ``"`` ``"`` (many1Chars anyChar) 

    // key in a collection
    let toml_key          : Parser<_> = 
        (choiceL [pBareKey; pQuoteKey ] 
            "a quoted key starting with \"\
             or a bare key starting with a letter or digit\n") .>> skip_tspcs

    // toplevel keys
    let pTableKey         : Parser<_> = between ``[``   ``]`` (sepBy toml_key ``.``)
    let pArrayOfTablesKey : Parser<_> = between ``[[`` ``]]`` (sepBy toml_key ``.``)


    (*--------------------*)
    (* Collection Parsers *)
    (*--------------------*)

    // helper function for accumulating parser results in recursive low level parsers
    let inline checkReply (psr:Parser<_>) loop acc stream =
        let (reply: _ Reply) =  psr stream
        if reply.Status <> Ok then Reply (Error, reply.Error) 
        else loop (reply.Result::acc) stream


    // Forward declaration to allow mutually recursive 
    // parsers between arrays and inline tables
    let toml_value      , private pValueImpl  = createParserForwardedToRef ()
    let toml_array      , private pArrayImpl  = createParserForwardedToRef ()
    let toml_inlineTable, private pITblImpl   = createParserForwardedToRef ()

    let pKVP : Parser<_>  = skip_tspcs >>. toml_key .>>. (skipEqs >>. toml_value)

    let pArray_toml : Parser<_> = 
        let ``[``   : Parser<_> = (attempt (``[`` .>> unicodeNewline .>> skip_tspcs)) <|> ``[`` 
        let ``]``   : Parser<_> = (attempt (skip_tspcs .>> unicodeNewline .>> skip_tspcs >>. ``]``)) <|> ``]`` 
        let rec loop acc (stream:CharStream<_>) =
            match stream.Peek() with 
            | ']' -> Reply acc
            | ' ' | '\t' | ','  ->  stream.Skip()
                                    loop acc stream
            | '\n'  ->  stream.SkipUnicodeNewline()|> ignore
                        loop acc stream
            | _ -> checkReply  toml_value loop acc stream
        let psr : Parser<_> = loop []
        between ``[`` ``]`` psr

    let pInlineTable : Parser<_> =
        between ``{`` ``}`` (sepBy pKVP ``,``) |>> Table

    pArrayImpl := pArray_toml  |>> Value.Array       .>> skip_tspcs
    pITblImpl  := pInlineTable |>> Value.InlineTable .>> skip_tspcs

    // low level parser implementation for simple toml values
    /// parses strings, ints, floats, bools, datetimes, inline tables, and arrays
    let private value_parser (stream: CharStream<_>) =
        match stream.Peek() with
        | '{' -> toml_inlineTable   stream
        | '[' -> toml_array         stream
        | '"' | '\'' -> toml_string stream
        | 't' | 'f'  -> toml_bool   stream
        | c when isDigit c ->
            // A Date time will always have a `-` at this position e.g. 
            if stream.Peek 4 = '-' then toml_datetime stream else
            let state, reply = stream.State, toml_int stream
            if reply.Status = Ok then reply else
            // if parsing for an int fails, backtrack and try a float
            stream.BacktrackTo state
            let reply = toml_float stream
            if reply.Status = Ok then reply else
            stream.BacktrackTo state
            Reply (Error, ErrorMessageList.Merge (expected "some kind of TOML value", reply.Error))
        | _ -> Reply (Error, expected "some kind of TOML value")

    pValueImpl := value_parser .>> skip_tspcs

        
    (*|------------------|*)
    (*| Toplevel Parsers |*)
    (*|------------------|*)


    // Brace matching parsers that store the chars unlike preceding versions
    let ``[+``  = pstring "["  .>> skip_tspcs 
    let ``]+``  = pstring "]"  .>> skip_tspcs
    let ``[[+`` = pstring "[[" .>> skip_tspcs 
    let ``]]+`` = pstring "]]" .>> skip_tspcs

    let ptkey : Parser<_> =  
        ``[+``.>>. (sepBy toml_key ``.``) .>>. ``]+``
        |>> fun ((b,ls),e) -> String.concat "" [b; listToKey ls; e]

    let paotKey : Parser<_> = 
        ``[[+``.>>. (sepBy toml_key ``.``) .>>. ``]]+``
        |>> fun ((b,ls),e) -> String.concat "" [b; listToKey ls; e]

    let raw_content_block =
        manyCharsTill anyChar (followedByL (paotKey<|>ptkey) "expected a table or array table key"<|>eof)

    let toml_item = (toml_key .>>. (skipEqs >>. toml_value)) .>> skip_tspcs


    // split the TOML file up into sections by their table keys
    let section_splitter : Parser<_> =
        let rec loop acc (stream:CharStream<_>) =
            match stream.Peek () with
            | '['  ->   
                if stream.Peek2 () = TwoChars ('[','[') 
                then checkReply (paotKey.>>.raw_content_block) loop acc stream
                else checkReply (ptkey.>>.raw_content_block) loop acc stream 
            | '#' -> 
                stream.SkipRestOfLine true 
                loop acc stream
            | ' ' | '\t' -> 
                stream.SkipUnicodeWhitespace() |> ignore 
                loop acc stream 
            | '\n' -> 
                if not (stream.SkipUnicodeNewline ()) 
                then Reply acc 
                else loop acc stream
            | c when isKeyStart c -> stream.SkipRestOfLine true; loop acc stream
            | c ->   
                if stream.IsEndOfStream 
                then Reply acc else
                Reply (Error, expected <| sprintf  "could not parse unexpected character -'%c'\
                                                    at Ln: %d Col: %d" c stream.Line stream.Column)
        loop [] 


    let toml_section, private pSectionImpl  = createParserForwardedToRef ()

    let private section_parser : Parser<_> =
        let rec loop acc (stream:CharStream<_>) =
            match stream.Peek () with
            | '#' ->  stream.SkipRestOfLine true;  loop acc stream
            | ' ' | '\t' -> stream.SkipUnicodeWhitespace () |> ignore; loop acc stream 
            | '\n' ->       stream.SkipUnicodeNewline () |> ignore; loop acc stream
            | '['  -> Reply acc  
            | c when isKeyStart c -> checkReply toml_item loop acc stream
            | c -> 
                if stream.IsEndOfStream then Reply acc else
                Reply (Error, expected <| sprintf "could not parse unexpected character -'%c'\
                                                   at Ln: %d Col: %d" c stream.Line stream.Column)
        loop [] 

    pSectionImpl := section_parser .>> skip_tspcs


    let inline isAoT header = String.bookends "[[" "]]" header

    let parse_block (keybracket:string, sectiondata:string) =
        let tkey  = if isAoT keybracket then keybracket.[2..keybracket.Length-3] else 
                    keybracket.[1..keybracket.Length-2]
        match run toml_section sectiondata  with
        | Failure (errorMsg,_,_) -> failwith errorMsg
        | Success (result,_,_)   -> 
            keybracket, result |> List.map (fun (k,v) -> String.concat""[tkey;".";k],v)



    let inline extract (tables:(string*(string*Value)list)[]) =
        if tables = [||] then [||],[||] else    
        let key = (Array.head>>fst) tables
        let root = getRoot key
        let filter = 
            // prevents combining two different arrays of tables
            if isAoT key then 
                fst>>(isAoT|?|(getRoot>>((=)root)))
            else 
                // only extract groups of tables with the same root to make construction easier
                // filter empty tables (empty array of tables can't be filtered)
                fst>>((not<<isAoT)|&|(getRoot>>(=)root))
        let took =       
            match Array.takeWhile filter tables  with 
            | xs when isAoT key -> xs
            | [|_,[]|] -> [||]
            | xs -> xs
        let skipped = Array.skipWhile filter tables
        took, skipped


    let rec construct_aot (tableName:string) (parseData:(string*(string*Value) list)[]) =
        let tableName = getRoot tableName
        let parseData = Array.rev parseData

        let makeTable (elems:(string*Value) list) =
            (Table (), elems) ||> List.fold (fun tbl (k,v) -> tbl.Add (keyName k,v)|>ignore; tbl)

        let foldAoT (prev:string, aotAcc:Table list) (cur:string, elems:(string*Value) list)  =

            let key = keyName cur
            match prev, cur with
            | _, cur when cleanEnds cur = tableName ->
                (cur, (makeTable elems)::aotAcc)
            | prev, cur when parentKey cur = tableName  && isAoT cur -> 
                // case where an AoT needs to be created from scratch because this is nested in the first table in the AoT
                match aotAcc with
                | [] -> 
                    let tbl = Table ()
                    tbl.Add (key, Value.ArrayOfTables[(makeTable elems)])|>ignore
                    (cur, [tbl])
                | hd::_ ->
                    if hd.ContainsKey key then
                        match hd.Elem key with
                        | Value.ArrayOfTables ls -> 
                            aotAcc.Head.Elem key <- Value.ArrayOfTables((makeTable elems)::ls)
                        | _ -> ()
                    else
                        hd.Add (key, Value.ArrayOfTables[(makeTable elems)])|>ignore
                    (prev,(makeTable elems)::aotAcc)
            | prev ,cur when prev = cur && cleanEnds cur = tableName -> 
                (cur, (makeTable elems)::aotAcc)
            | prev, cur when parentKey cur = tableName && (not<<isAoT) cur ->  
                aotAcc.Head.Add (key, (makeTable elems))|> ignore
                (prev, aotAcc)

            | prev,cur ->   failwithf "reached unhandled Array of Table Case with - \n\
                                       %s\nprev - %s\ncurrent - %s\n%A" tableName prev cur elems
        (("",[]),parseData) ||> Array.fold foldAoT 
        |> fun (_,v) -> 
            Value.ArrayOfTables v



    let construct_toml (toplevel:(string*Value) list, 
                        blocks:(string*(string*Value) list)[]) =
        let construct_table (toml:Table) (name:string,elems:(string*Value) list) =
            if toml.ContainsKey  name then toml else
            elems |> List.iter(fun (k,v)-> toml.Add(k,v)|>ignore )
            toml

        let rec addExtractedBlocks (tables:(string*(string*Value) list)[]) (toml:Table) =
            let addelems elems =
                let name = (Array.head>>fst) elems
                if isAoT name then 
                    let aot = construct_aot name elems
                    toml.Add (getRoot name,aot) |> ignore
                    toml
                else          
                    let toml = Array.fold construct_table  toml elems
                    toml
            match extract tables with
            | [||], [||]  -> toml
            | [||], rest  -> addExtractedBlocks rest toml
            | elems, [||] -> addelems elems
            | elems, rest -> addExtractedBlocks rest (addelems elems)
    
        let rec addTopElems (ls:(string*Value)list) (toml:Table) = 
            match ls with 
            | [] -> toml
            |[k,_] when isNull k -> toml
            | (k,v)::tl -> 
                toml.Add(k,v)|>ignore
                addTopElems tl toml

        Table() |> addTopElems toplevel |> addExtractedBlocks blocks 


    let internal sprint_parse_array (toplevel,tables) =
        let sb = StringBuilder()
    
        let findMaxes (kvpls:(string*Value) list) =
            if kvpls = [] then 0,0 else
            ((0,0),kvpls) 
            ||> Seq.fold (fun (kmax,vmax) (key,value) ->
                max kmax key.Length, max vmax (string value).Length)

        let format_section ls =
            match findMaxes ls with
            | 0,0 -> ()
            | maxklen, maxvlen -> 
                ls |> List.iter (fun (key,value) ->
                    sprintf "%-*s = %-*s" maxklen key maxvlen (string value)
                    |> sb.AppendLine |> ignore )
        tables 
        |> Array.map (fun x -> printfn "%A" x; x)
        |> Array.iter (fun (name,kvpls) -> 
            sb.AppendLine().AppendLine(name).AppendLine()|>ignore
            match kvpls with [] -> () | _  -> format_section kvpls)

        sb.AppendLine().AppendLine("-- toplevel --").AppendLine()|>ignore
        format_section toplevel
        string sb


    let parse_toml_table =
        toml_section .>>. (section_splitter |>> fun ls ->
            Array.ofList ls |> Array.Parallel.map parse_block)
        |>> construct_toml


    let parse_to_print = 
        toml_section .>>. (section_splitter |>> fun ls ->
            Array.ofList ls |> Array.Parallel.map parse_block)
        |>> sprint_parse_array 
    
[<AutoOpen>]
module Read =
    open Parsers
    /// Read TOML data out of a file at `path`
    let readTomlFile (path:string) =
        match runParserOnFile parse_toml_table () path (Text.Encoding.UTF8) with
        | Failure (errorMsg,_,_) -> failwith errorMsg
        | Success (result,_,_)   -> result

    /// Read TOML data out of a string
    let readTomlString (text:string) =
        match runParserOnString parse_toml_table () "toml" text with
        | Failure (errorMsg,_,_) -> failwith errorMsg
        | Success (result,_,_)   -> result
