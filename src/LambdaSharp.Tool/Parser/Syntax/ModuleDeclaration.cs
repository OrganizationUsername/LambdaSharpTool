/*
 * LambdaSharp (λ#)
 * Copyright (C) 2018-2019
 * lambdasharp.net
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;

namespace LambdaSharp.Tool.Parser.Syntax {

    public class ModuleDeclaration : ADeclaration {

        //--- Properties ---
        public override string Keyword => "Module";

        [SyntaxKeyword()]
        public StringLiteral Module { get; set; }

        [SyntaxOptional]
        public StringLiteral Version { get; set; }

        [SyntaxOptional]
        public StringLiteral Description { get; set; }

        [SyntaxOptional]
        public DeclarationList<PragmaExpression> Pragmas { get; set; }

        [SyntaxOptional]
        public DeclarationList<StringLiteral> Secrets { get; set; }

        [SyntaxOptional]
        public DeclarationList<UsingDeclaration> Using { get; set; }

        [SyntaxRequired]
        public DeclarationList<AItemDeclaration> Items { get; set; }
    }

    public class UsingDeclaration : ADeclaration {

        //--- Properties ---
        public override string Keyword => "Module";

        [SyntaxKeyword]
        public StringLiteral Module { get; set; }

        [SyntaxOptional]
        public StringLiteral Description { get; set; }
    }
}