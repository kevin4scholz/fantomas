﻿module Fantomas.Core.Flowering

open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Text
open Fantomas.Core.FormatConfig
open Fantomas.Core.ISourceTextExtensions
open Fantomas.Core.SyntaxOak

let internal collectTriviaFromCodeComments (source: ISourceText) (codeComments: CommentTrivia list) : TriviaNode list =
    codeComments
    |> List.map (function
        | CommentTrivia.BlockComment _ -> failwith "todo, E75069C0-FBC0-4026-9C0D-5BF0773A606F"
        | CommentTrivia.LineComment r ->
            let content = source.GetContentAt r
            let index = r.StartLine - 1
            let line = source.GetLineString index

            let content =
                let trimmedLine = line.TrimStart(' ', ';')

                if index = 0 && trimmedLine.StartsWith("#!") then // shebang
                    CommentOnSingleLine content
                else if trimmedLine.StartsWith("//") then
                    CommentOnSingleLine content
                else
                    failwith "todo, 220DE805-1EDF-426C-9138-91A517DF6726"

            TriviaNode(content, r))

let internal collectTriviaFromBlankLines
    (config: FormatConfig)
    (source: ISourceText)
    (rootNode: NodeBase)
    (codeComments: CommentTrivia list)
    (codeRange: range)
    : TriviaNode list =
    if codeRange.StartLine = 0 && codeRange.EndLine = 0 then
        // weird edge cases where there is no source code but only hash defines
        []
    else
        let fileIndex = codeRange.FileIndex

        let captureLinesIfMultiline (r: range) =
            if r.StartLine = r.EndLine then
                []
            else
                [ r.StartLine .. r.EndLine ]

        let multilineStringsLines =
            let rec visit (node: NodeBase) (finalContinuation: int list -> int list) =
                let continuations: ((int list -> int list) -> int list) list =
                    Array.toList node.Children |> List.map visit

                let currentLines =
                    match node with
                    | :? StringNode as node -> captureLinesIfMultiline node.Range
                    | _ -> []

                let finalContinuation (lines: int list list) : int list =
                    List.collect id (currentLines :: lines) |> finalContinuation

                Continuation.sequence continuations finalContinuation

            visit rootNode id

        let blockCommentLines =
            codeComments
            |> List.collect (function
                | CommentTrivia.BlockComment r -> captureLinesIfMultiline r
                | CommentTrivia.LineComment _ -> [])

        let ignoreLines =
            Set(
                seq {
                    yield! multilineStringsLines
                    yield! blockCommentLines
                }
            )

        let min = System.Math.Max(0, codeRange.StartLine - 1)

        let max = System.Math.Min(source.Length - 1, codeRange.EndLine - 1)

        (min, [ min..max ])
        ||> List.chooseState (fun count idx ->
            if ignoreLines.Contains(idx + 1) then
                0, None
            else
                let line = source.GetLineString(idx)

                if String.isNotNullOrWhitespace line then
                    0, None
                else
                    let range =
                        let p = Position.mkPos (idx + 1) 0
                        Range.mkFileIndexRange fileIndex p p

                    if count < config.KeepMaxNumberOfBlankLines then
                        (count + 1), Some(TriviaNode(Newline, range))
                    else
                        count, None)

let rec findNodeWhereRangeFitsIn (root: NodeBase) (range: range) : NodeBase option =
    let doesSelectionFitInNode = RangeHelpers.rangeContainsRange root.Range range

    if not doesSelectionFitInNode then
        None
    else
        // The more specific the node fits the selection, the better
        let betterChildNode =
            root.Children
            |> Array.choose (fun childNode -> findNodeWhereRangeFitsIn childNode range)
            |> Array.tryHead

        match betterChildNode with
        | Some betterChild -> Some betterChild
        | None -> Some root

let triviaBeforeOrAfterEntireTree (rootNode: NodeBase) (trivia: TriviaNode) : unit =
    let isBefore = trivia.Range.EndLine < rootNode.Range.StartLine

    if isBefore then
        rootNode.AddBefore(trivia)
    else
        rootNode.AddAfter(trivia)

let simpleTriviaToTriviaInstruction (containerNode: NodeBase) (trivia: TriviaNode) : unit =
    containerNode.Children
    |> Array.tryFind (fun node -> node.Range.StartLine > trivia.Range.StartLine)
    |> Option.map (fun n -> n.AddBefore)
    |> Option.orElseWith (fun () -> Array.tryLast containerNode.Children |> Option.map (fun n -> n.AddAfter))
    |> Option.iter (fun f -> f trivia)

let addToTree (tree: Oak) (trivia: TriviaNode seq) =
    for trivia in trivia do
        let smallestNodeThatContainsTrivia = findNodeWhereRangeFitsIn tree trivia.Range

        match smallestNodeThatContainsTrivia with
        | None -> triviaBeforeOrAfterEntireTree tree trivia
        | Some parentNode -> simpleTriviaToTriviaInstruction parentNode trivia

let enrichTree (config: FormatConfig) (sourceText: ISourceText) (ast: ParsedInput) (tree: Oak) : Oak =
    let _directives, codeComments =
        match ast with
        | ParsedInput.ImplFile (ParsedImplFileInput(trivia = { ConditionalDirectives = directives
                                                               CodeComments = codeComments })) ->
            directives, codeComments
        | ParsedInput.SigFile (ParsedSigFileInput(trivia = { ConditionalDirectives = directives
                                                             CodeComments = codeComments })) -> directives, codeComments

    let trivia =
        let newlines =
            collectTriviaFromBlankLines config sourceText tree codeComments tree.Range

        let comments =
            match ast with
            | ParsedInput.ImplFile (ParsedImplFileInput (trivia = trivia)) ->
                collectTriviaFromCodeComments sourceText trivia.CodeComments
            | ParsedInput.SigFile (ParsedSigFileInput (trivia = trivia)) ->
                collectTriviaFromCodeComments sourceText trivia.CodeComments

        [| yield! comments; yield! newlines |]
        |> Array.sortBy (fun n -> n.Range.Start.Line, n.Range.Start.Column)

    addToTree tree trivia
    tree