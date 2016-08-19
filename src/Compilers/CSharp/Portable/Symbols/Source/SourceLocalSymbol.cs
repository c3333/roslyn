﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Represents a local variable in a method body.
    /// </summary>
    internal class SourceLocalSymbol : LocalSymbol
    {
        protected readonly Binder binder;

        /// <summary>
        /// Might not be a method symbol.
        /// </summary>
        private readonly Symbol _containingSymbol;

        private readonly SyntaxToken _identifierToken;
        private readonly ImmutableArray<Location> _locations;
        private readonly RefKind _refKind;
        private readonly TypeSyntax _typeSyntax;
        private readonly LocalDeclarationKind _declarationKind;
        private TypeSymbol _type;

        /// <summary>
        /// There are three ways to initialize a fixed statement local:
        ///   1) with an address;
        ///   2) with an array (or fixed-size buffer); or
        ///   3) with a string.
        /// 
        /// In the first two cases, the resulting local will be emitted with a "pinned" modifier.
        /// In the third case, it is not the fixed statement local but a synthesized temp that is pinned.  
        /// Unfortunately, we can't distinguish these cases when the local is declared; we only know
        /// once we have bound the initializer.
        /// </summary>
        /// <remarks>
        /// CompareExchange doesn't support bool, so use an int.  First bit is true/false, second bit 
        /// is read/unread (debug-only).
        /// </remarks>
        private int _isSpecificallyNotPinned;

        private SourceLocalSymbol(
            Symbol containingSymbol,
            Binder binder,
            bool allowRefKind,
            TypeSyntax typeSyntax,
            SyntaxToken identifierToken,
            LocalDeclarationKind declarationKind)
        {
            Debug.Assert(identifierToken.Kind() != SyntaxKind.None);
            Debug.Assert(declarationKind != LocalDeclarationKind.None);
            Debug.Assert(binder != null);

            this.binder = binder;
            this._containingSymbol = containingSymbol;
            this._identifierToken = identifierToken;
            this._typeSyntax = allowRefKind ? typeSyntax.SkipRef(out this._refKind) : typeSyntax;
            this._declarationKind = declarationKind;

            // create this eagerly as it will always be needed for the EnsureSingleDefinition
            _locations = ImmutableArray.Create<Location>(identifierToken.GetLocation());
        }

        /// <summary>
        /// A binder for the local's scope that is used to check for name conflicts.
        /// </summary>
        internal Binder Binder
        {
            get { return binder; }
        }

        /// <summary>
        /// Make a local variable symbol for a foreach iteration variable that can be inferred
        /// (if necessary) by binding the collection element type of a foreach loop.
        /// </summary>
        public static SourceLocalSymbol MakeForeachLocal(
            MethodSymbol containingMethod,
            ForEachLoopBinder binder,
            TypeSyntax typeSyntax,
            SyntaxToken identifierToken,
            ExpressionSyntax collection)
        {
            return new ForEachLocalSymbol(containingMethod, binder, typeSyntax, identifierToken, collection, LocalDeclarationKind.ForEachIterationVariable);
        }

        /// <summary>
        /// Make a local variable symbol for an element of a deconstruction,
        /// which can be inferred (if necessary) by binding the enclosing statement.
        /// </summary>
        public static SourceLocalSymbol MakeDeconstructionLocal(
            Symbol containingSymbol,
            Binder binder,
            TypeSyntax closestTypeSyntax,
            SyntaxToken identifierToken,
            LocalDeclarationKind kind,
            SyntaxNode deconstruction)
        {
            Debug.Assert(closestTypeSyntax != null);

            Debug.Assert(closestTypeSyntax.Kind() != SyntaxKind.RefType);
            if (closestTypeSyntax.IsVar)
            {
                return new DeconstructionLocalSymbol(
                    containingSymbol, binder, closestTypeSyntax, identifierToken, kind, deconstruction);
            }
            else
            {
                return new SourceLocalSymbol(containingSymbol, binder, false, closestTypeSyntax, identifierToken, kind);
            }
        }

        /// <summary>
        /// Make a pattern local variable symbol which can be inferred (if necessary) by binding the enclosing pattern-matching operation.
        /// </summary>
        internal static LocalSymbol MakePatternLocalSymbol(
            Symbol containingSymbol,
            Binder binder,
            DeclarationPatternSyntax node)
        {
            Debug.Assert(node.Type.Kind() != SyntaxKind.RefType);
            if (!node.Type.IsVar)
            {
                return new SourceLocalSymbol(containingSymbol, binder, false, node.Type, node.Identifier, LocalDeclarationKind.PatternVariable);
            }

            //
            // A pattern variable's type can be inferred from context.
            //
            // The syntax allows a pattern either (1) on the right-hand-side of an "is-pattern" expression, or
            // (2) in the pattern of a switch case. These have their type inferred using different mechanisms,
            // so we need to find that context. These are the only cases the parser will produce, so we can be
            // confident that we will find one of them by scanning up node's .Parent chain. We allow
            // a NullReferenceException at n.Parent to occur when we are fed bad trees.
            //
            for (SyntaxNode n = node; ; n = n.Parent)
            {
                var kind = n.Kind();
                if (kind == SyntaxKind.IsPatternExpression)
                {
                    var expr = (IsPatternExpressionSyntax)n;
                    return new VariableDeclaredInExpression(containingSymbol, binder, binder, node.Type, node.Identifier, LocalDeclarationKind.PatternVariable, expr);
                }
                else if (kind == SyntaxKind.CasePatternSwitchLabel)
                {
                    var label = (CasePatternSwitchLabelSyntax)n;
                    return new SwitchPatternLocalSymbol(containingSymbol, binder, node.Type, node.Identifier, label);
                }
            }
        }

        /// <summary>
        /// Make a local variable symbol which can be inferred (if necessary) by binding its initializing expression.
        /// </summary>
        public static SourceLocalSymbol MakeLocal(
            Symbol containingSymbol,
            Binder binder,
            bool allowRefKind,
            TypeSyntax typeSyntax,
            SyntaxToken identifierToken,
            LocalDeclarationKind declarationKind,
            EqualsValueClauseSyntax initializer = null)
        {
            Debug.Assert(declarationKind != LocalDeclarationKind.ForEachIterationVariable);
            if (initializer == null)
            {
                return new SourceLocalSymbol(containingSymbol, binder, allowRefKind, typeSyntax, identifierToken, declarationKind);
            }

            return new LocalWithInitializer(containingSymbol, binder, typeSyntax, identifierToken, initializer, declarationKind);
        }

        /// <summary>
        /// Make a local variable for an out variable declaration whose type is inferred as a side-effect of binding the enclosing expression.
        /// </summary>
        /// <param name="containingSymbol"></param>
        /// <param name="scopeBinder">The binder for the scope of the local</param>
        /// <param name="invocationBinderOpt">The binder for the invocation, if different from scopeBinder</param>
        /// <param name="typeSyntax">The type syntax</param>
        /// <param name="identifierToken"></param>
        /// <param name="declarationKind"></param>
        /// <param name="context">The expression to be bound, which will result in the variable receiving a type</param>
        /// <returns></returns>
        public static SourceLocalSymbol MakeVariableDeclaredInExpression(
            Symbol containingSymbol,
            Binder scopeBinder,
            Binder invocationBinderOpt,
            TypeSyntax typeSyntax,
            SyntaxToken identifierToken,
            LocalDeclarationKind declarationKind,
            ExpressionSyntax context)
        {
            Debug.Assert(typeSyntax.Kind() != SyntaxKind.RefType);
            return typeSyntax.IsVar
                ? new VariableDeclaredInExpression(containingSymbol, scopeBinder, invocationBinderOpt ?? scopeBinder, typeSyntax, identifierToken, declarationKind, context)
                : new SourceLocalSymbol(containingSymbol, scopeBinder, false, typeSyntax, identifierToken, declarationKind);
        }

        /// <summary>
        /// Make a local variable for an out variable declaration that can be inferred by binding a constructor initializer.
        /// </summary>
        /// <param name="containingSymbol"></param>
        /// <param name="scopeBinder">The binder for the scope of the local</param>
        /// <param name="typeSyntax"></param>
        /// <param name="identifierToken"></param>
        /// <param name="context">The expression to be bound, which will result in the variable receiving a type</param>
        /// <returns></returns>
        public static SourceLocalSymbol MakeOutVariableInCtorInitializer(
            Symbol containingSymbol,
            Binder scopeBinder,
            TypeSyntax typeSyntax,
            SyntaxToken identifierToken,
            ConstructorInitializerSyntax context)
        {
            Debug.Assert(typeSyntax.Kind() != SyntaxKind.RefType);
            return typeSyntax.IsVar
                ? new OutVariableInCtorInitializer(containingSymbol, scopeBinder, typeSyntax, identifierToken, context)
                : new SourceLocalSymbol(containingSymbol, scopeBinder, false, typeSyntax, identifierToken, LocalDeclarationKind.RegularVariable);
        }

        internal override bool IsImportedFromMetadata
        {
            get { return false; }
        }

        internal override LocalDeclarationKind DeclarationKind
        {
            get { return _declarationKind; }
        }

        internal override SynthesizedLocalKind SynthesizedKind
        {
            get { return SynthesizedLocalKind.UserDefined; }
        }

        internal override LocalSymbol WithSynthesizedLocalKindAndSyntax(SynthesizedLocalKind kind, SyntaxNode syntax)
        {
            throw ExceptionUtilities.Unreachable;
        }

        internal override bool IsPinned
        {
            get
            {
#if DEBUG
                if ((_isSpecificallyNotPinned & 2) == 0)
                {
                    Interlocked.CompareExchange(ref _isSpecificallyNotPinned, _isSpecificallyNotPinned | 2, _isSpecificallyNotPinned);
                    Debug.Assert((_isSpecificallyNotPinned & 2) == 2, "Regardless of which thread won, the read bit should be set.");
                }
#endif
                return _declarationKind == LocalDeclarationKind.FixedVariable && (_isSpecificallyNotPinned & 1) == 0;
            }
        }

        internal void SetSpecificallyNotPinned()
        {
            Debug.Assert((_isSpecificallyNotPinned & 2) == 0, "Shouldn't be writing after first read.");
            Interlocked.CompareExchange(ref _isSpecificallyNotPinned, _isSpecificallyNotPinned | 1, _isSpecificallyNotPinned);
            Debug.Assert((_isSpecificallyNotPinned & 1) == 1, "Regardless of which thread won, the flag bit should be set.");
        }

        internal virtual void SetReturnable()
        {
            throw ExceptionUtilities.Unreachable;
        }

        public override Symbol ContainingSymbol
        {
            get { return _containingSymbol; }
        }

        /// <summary>
        /// Gets the name of the local variable.
        /// </summary>
        public override string Name
        {
            get
            {
                return _identifierToken.ValueText;
            }
        }

        // Get the identifier token that defined this local symbol. This is useful for robustly
        // checking if a local symbol actually matches a particular definition, even in the presence
        // of duplicates.
        internal override SyntaxToken IdentifierToken
        {
            get
            {
                return _identifierToken;
            }
        }

        public override TypeSymbol Type
        {
            get
            {
                if ((object)_type == null)
                {
                    TypeSymbol localType = GetTypeSymbol();
                    SetType(localType);
                }

                return _type;
            }
        }

        public bool IsVar
        {
            get
            {
                if (_typeSyntax == null)
                {
                    // in "let x = 1;" there is no syntax corresponding to the type.
                    return true;
                }

                if (_typeSyntax.IsVar)
                {
                    bool isVar;
                    TypeSymbol declType = this.binder.BindType(_typeSyntax, new DiagnosticBag(), out isVar);
                    return isVar;
                }

                return false;
            }
        }

        private TypeSymbol GetTypeSymbol()
        {
            var diagnostics = DiagnosticBag.GetInstance();

            Binder typeBinder = this.binder;

            bool isVar;
            RefKind refKind;
            TypeSymbol declType = typeBinder.BindType(_typeSyntax.SkipRef(out refKind), diagnostics, out isVar);

            if (isVar)
            {
                TypeSymbol inferredType = InferTypeOfVarVariable(diagnostics);

                // If we got a valid result that was not void then use the inferred type
                // else create an error type.
                if ((object)inferredType != null &&
                    inferredType.SpecialType != SpecialType.System_Void)
                {
                    declType = inferredType;
                }
                else
                {
                    declType = typeBinder.CreateErrorType("var");
                }
            }

            Debug.Assert((object)declType != null);

            //
            // Note that we drop the diagnostics on the floor! That is because this code is invoked mainly in
            // IDE scenarios where we are attempting to use the types of a variable before we have processed
            // the code which causes the variable's type to be inferred. In batch compilation, on the
            // other hand, local variables have their type inferred, if necessary, in the course of binding
            // the statements of a method from top to bottom, and an inferred type is given to a variable
            // before the variable's type is used by the compiler.
            //
            diagnostics.Free();
            return declType;
        }

        protected virtual TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
        {
            // TODO: this method must be overridden for pattern variables to bind the
            // expression or statement that is the nearest enclosing to the pattern variable's
            // declaration. That will cause the type of the pattern variable to be set as a side-effect.
            return _type;
        }

        internal void SetType(TypeSymbol newType)
        {
            TypeSymbol originalType = _type;

            // In the event that we race to set the type of a local, we should
            // always deduce the same type, or deduce that the type is an error.

            Debug.Assert((object)originalType == null ||
                originalType.IsErrorType() && newType.IsErrorType() ||
                originalType == newType);

            if ((object)originalType == null)
            {
                Interlocked.CompareExchange(ref _type, newType, null);
            }
        }

        /// <summary>
        /// Gets the locations where the local symbol was originally defined in source.
        /// There should not be local symbols from metadata, and there should be only one local variable declared.
        /// TODO: check if there are multiple same name local variables - error symbol or local symbol?
        /// </summary>
        public override ImmutableArray<Location> Locations
        {
            get
            {
                return _locations;
            }
        }

        internal sealed override SyntaxNode GetDeclaratorSyntax()
        {
            return _identifierToken.Parent;
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get
            {
                SyntaxNode node = _identifierToken.Parent;
#if DEBUG
                switch (_declarationKind)
                {
                    case LocalDeclarationKind.RegularVariable:
                    case LocalDeclarationKind.ForInitializerVariable:
                        Debug.Assert(node is VariableDeclaratorSyntax || node is SingleVariableDesignationSyntax);
                        break;

                    case LocalDeclarationKind.Constant:
                    case LocalDeclarationKind.FixedVariable:
                    case LocalDeclarationKind.UsingVariable:
                        Debug.Assert(node is VariableDeclaratorSyntax);
                        break;

                    case LocalDeclarationKind.ForEachIterationVariable:
                        Debug.Assert(node is ForEachStatementSyntax || node is SingleVariableDesignationSyntax);
                        break;

                    case LocalDeclarationKind.CatchVariable:
                        Debug.Assert(node is CatchDeclarationSyntax);
                        break;

                    case LocalDeclarationKind.PatternVariable:
                        Debug.Assert(node is DeclarationPatternSyntax);
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(_declarationKind);
                }
#endif
                return ImmutableArray.Create(node.GetReference());
            }
        }

        internal override bool IsCompilerGenerated
        {
            get { return false; }
        }

        internal override ConstantValue GetConstantValue(SyntaxNode node, LocalSymbol inProgress, DiagnosticBag diagnostics)
        {
            return null;
        }

        internal override ImmutableArray<Diagnostic> GetConstantValueDiagnostics(BoundExpression boundInitValue)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        internal override RefKind RefKind
        {
            get { return _refKind; }
        }

        public sealed override bool Equals(object obj)
        {
            if (obj == (object)this)
            {
                return true;
            }

            var symbol = obj as SourceLocalSymbol;
            return (object)symbol != null
                && symbol._identifierToken.Equals(_identifierToken)
                && Equals(symbol._containingSymbol, _containingSymbol);
        }

        public sealed override int GetHashCode()
        {
            return Hash.Combine(_identifierToken.GetHashCode(), _containingSymbol.GetHashCode());
        }

        /// <summary>
        /// Symbol for a local whose type can be inferred by binding its initializer.
        /// </summary>
        private sealed class LocalWithInitializer : SourceLocalSymbol
        {
            private readonly EqualsValueClauseSyntax _initializer;

            /// <summary>
            /// Store the constant value and the corresponding diagnostics together
            /// to avoid having the former set by one thread and the latter set by
            /// another.
            /// </summary>
            private EvaluatedConstant _constantTuple;

            /// <summary>
            /// Unfortunately we can only know a ref local is returnable after binding the initializer.
            /// </summary>
            private bool _returnable;

            public LocalWithInitializer(
                Symbol containingSymbol,
                Binder binder,
                TypeSyntax typeSyntax,
                SyntaxToken identifierToken,
                EqualsValueClauseSyntax initializer,
                LocalDeclarationKind declarationKind) :
                    base(containingSymbol, binder, true, typeSyntax, identifierToken, declarationKind)
            {
                Debug.Assert(declarationKind != LocalDeclarationKind.ForEachIterationVariable);
                Debug.Assert(initializer != null);

                _initializer = initializer;

                // byval locals are always returnable
                // byref locals with initializers are assumed not returnable unless proven otherwise
                // NOTE: if we assumed returnable, then self-referring initializer could result in 
                //       a randomly changing returnability when initializer is bound concurrently.
                _returnable = this.RefKind == RefKind.None;
            }

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                // Since initializer might use Out Variable Declarations and Pattern Variable Declarations, we need to find 
                // the right binder to use for the initializer.
                // Climb up the syntax tree looking for a first binder that we can find, but stop at the first statement syntax.
                CSharpSyntaxNode currentNode = _initializer;
                Binder initializerBinder;

                do
                {
                    initializerBinder = this.binder.GetBinder(currentNode);

                    if (initializerBinder != null || currentNode is StatementSyntax)
                    {
                        break;
                    }

                    currentNode = currentNode.Parent;   
                }
                while (currentNode != null);

#if DEBUG
                Binder parentBinder = initializerBinder;

                while (parentBinder != null)
                {
                    if (parentBinder == this.binder)
                    {
                        break;
                    }

                    parentBinder = parentBinder.Next;
                }

                Debug.Assert(parentBinder != null);
#endif 

                var newBinder = initializerBinder ?? this.binder;
                var initializerOpt = newBinder.BindInferredVariableInitializer(diagnostics, RefKind, _initializer, _initializer);
                return initializerOpt?.Type;
            }

            internal override SyntaxNode ForbiddenZone => _initializer;

            /// <summary>
            /// Determine the constant value of this local and the corresponding diagnostics.
            /// Set both to constantTuple in a single operation for thread safety.
            /// </summary>
            /// <param name="inProgress">Null for the initial call, non-null if we are in the process of evaluating a constant.</param>
            /// <param name="boundInitValue">If we already have the bound node for the initial value, pass it in to avoid recomputing it.</param>
            private void MakeConstantTuple(LocalSymbol inProgress, BoundExpression boundInitValue)
            {
                if (this.IsConst && _constantTuple == null)
                {
                    var value = Microsoft.CodeAnalysis.ConstantValue.Bad;
                    var initValueNodeLocation = _initializer.Value.Location;
                    var diagnostics = DiagnosticBag.GetInstance();
                    Debug.Assert(inProgress != this);
                    var type = this.Type;
                    if (boundInitValue == null)
                    {
                        var inProgressBinder = new LocalInProgressBinder(this, this.binder);
                        boundInitValue = inProgressBinder.BindVariableOrAutoPropInitializer(_initializer, this.RefKind, type, diagnostics);
                    }

                    value = ConstantValueUtils.GetAndValidateConstantValue(boundInitValue, this, type, initValueNodeLocation, diagnostics);
                    Interlocked.CompareExchange(ref _constantTuple, new EvaluatedConstant(value, diagnostics.ToReadOnlyAndFree()), null);
                }
            }

            internal override ConstantValue GetConstantValue(SyntaxNode node, LocalSymbol inProgress, DiagnosticBag diagnostics = null)
            {
                if (this.IsConst && inProgress == this)
                {
                    if (diagnostics != null)
                    {
                        diagnostics.Add(ErrorCode.ERR_CircConstValue, node.GetLocation(), this);
                    }

                    return Microsoft.CodeAnalysis.ConstantValue.Bad;
                }

                MakeConstantTuple(inProgress, boundInitValue: null);
                return _constantTuple == null ? null : _constantTuple.Value;
            }

            internal override ImmutableArray<Diagnostic> GetConstantValueDiagnostics(BoundExpression boundInitValue)
            {
                Debug.Assert(boundInitValue != null);
                MakeConstantTuple(inProgress: null, boundInitValue: boundInitValue);
                return _constantTuple == null ? ImmutableArray<Diagnostic>.Empty : _constantTuple.Diagnostics;
            }

            internal override void SetReturnable()
            {
                _returnable = true;
            }

            internal override bool IsReturnable
            {
                get
                {
                    return _returnable;
                }
            }
        }

        /// <summary>
        /// Symbol for a foreach iteration variable that can be inferred by binding the
        /// collection element type of the foreach.
        /// </summary>
        private sealed class ForEachLocalSymbol : SourceLocalSymbol
        {
            private readonly ExpressionSyntax _collection;

            public ForEachLocalSymbol(
                Symbol containingSymbol,
                ForEachLoopBinder binder,
                TypeSyntax typeSyntax,
                SyntaxToken identifierToken,
                ExpressionSyntax collection,
                LocalDeclarationKind declarationKind) :
                    base(containingSymbol, binder, false, typeSyntax, identifierToken, declarationKind)
            {
                Debug.Assert(declarationKind == LocalDeclarationKind.ForEachIterationVariable);
                _collection = collection;
            }

            /// <summary>
            /// We initialize the base's binder with a ForEachLoopBinder, so it is safe
            /// to cast it to that type here.
            /// </summary>
            private ForEachLoopBinder ForEachLoopBinder => (ForEachLoopBinder)base.binder;

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                return ForEachLoopBinder.InferCollectionElementType(diagnostics, _collection);
            }

            internal override SyntaxNode ForbiddenZone => _collection;
        }

        /// <summary>
        /// Symbol for a variable that can be inferred by binding an expression.
        /// </summary>
        private class VariableDeclaredInExpression : SourceLocalSymbol
        {
            private readonly ExpressionSyntax _expression;
            private readonly Binder _expressionBinder;

            public VariableDeclaredInExpression(
                Symbol containingSymbol,
                Binder scopeBinder,
                Binder expressionBinder,
                TypeSyntax typeSyntax,
                SyntaxToken identifierToken,
                LocalDeclarationKind declarationKind,
                ExpressionSyntax expression)
            : base(containingSymbol, scopeBinder, false, typeSyntax, identifierToken, declarationKind)
            {
                Debug.Assert(expression != null && expressionBinder != null);
                _expression = expression;
                _expressionBinder = expressionBinder;
            }

            internal override SyntaxNode ForbiddenZone => _expression;

            //
            // The forbidden zone for this type can only come into play for out variables, since they have a forbidden
            // zone that extends beyond the declaration point. For pattern variables, which are the other
            // kind of variable that can be typed based on an expression, we only need to bind the
            // immediately enclosing expression, which due to the shape of the syntax has the variable
            // declaration following any expression from which its value is inferred.
            //
            internal override ErrorCode ForbiddenDiagnostic => ErrorCode.ERR_ImplicitlyTypedOutVariableUsedInTheSameArgumentList;

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                // Bind the invocation to force inference.
                _expressionBinder.BindExpression(_expression, diagnostics);
                Debug.Assert((object)this._type != null);
                return this._type;
            }
        }

        /// <summary>
        /// Symbol for a variable that can be inferred by binding a pattern switch section label.
        /// </summary>
        private class SwitchPatternLocalSymbol : SourceLocalSymbol
        {
            private readonly CasePatternSwitchLabelSyntax _labelSyntax;

            public SwitchPatternLocalSymbol(
                Symbol containingSymbol, Binder binder, TypeSyntax type, SyntaxToken identifier, CasePatternSwitchLabelSyntax labelSyntax)
                : base(containingSymbol, binder, false, type, identifier, LocalDeclarationKind.PatternVariable)
            {
                this._labelSyntax = labelSyntax;
            }

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                // bind the switch label to cause inference of its pattern variables
                var sectionSyntax = (SwitchSectionSyntax)_labelSyntax.Parent;
                var switchStatement = (SwitchStatementSyntax)sectionSyntax.Parent;
                binder.GetBinder(switchStatement).BindPatternSwitchLabelForInference(_labelSyntax, diagnostics);
                Debug.Assert((object)this._type != null);
                return this._type;
            }
        }


        /// <summary>
        /// Symbol for an out variable that can be inferred by binding a ctor-initializer.
        /// </summary>
        private class OutVariableInCtorInitializer : SourceLocalSymbol
        {
            private readonly ConstructorInitializerSyntax _invocation;

            public OutVariableInCtorInitializer(
                Symbol containingSymbol,
                Binder scopeBinder,
                TypeSyntax typeSyntax,
                SyntaxToken identifierToken,
                ConstructorInitializerSyntax invocation)
            : base(containingSymbol, scopeBinder, false, typeSyntax, identifierToken, LocalDeclarationKind.RegularVariable)
            {
                Debug.Assert(invocation != null);
                _invocation = invocation;
            }

            internal override SyntaxNode ForbiddenZone => _invocation;

            internal override ErrorCode ForbiddenDiagnostic => ErrorCode.ERR_ImplicitlyTypedOutVariableUsedInTheSameArgumentList;

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                // Bind the ctor-initializer to force inference.
                this.binder.BindConstructorInitializer(_invocation.ArgumentList, (MethodSymbol)this.binder.ContainingMember(), diagnostics);
                Debug.Assert((object)this._type != null);
                return this._type;
            }
        }

        /// <summary>
        /// Symbol for a deconstruction local that might require type inference.
        /// For instance, local `x` in `var (x, y) = ...` or `(var x, int y) = ...`.
        /// </summary>
        private class DeconstructionLocalSymbol : SourceLocalSymbol
        {
            private readonly SyntaxNode _deconstruction;

            public DeconstructionLocalSymbol(
                Symbol containingSymbol,
                Binder binder,
                TypeSyntax typeSyntax,
                SyntaxToken identifierToken,
                LocalDeclarationKind declarationKind,
                SyntaxNode deconstruction)
            : base(containingSymbol, binder, false, typeSyntax, identifierToken, declarationKind)
            {
                _deconstruction = deconstruction;
            }

            protected override TypeSymbol InferTypeOfVarVariable(DiagnosticBag diagnostics)
            {
                // Try binding enclosing deconstruction-declaration (the top-level VariableDeclaration), this should force the inference.
                switch (_deconstruction.Kind())
                {
                    case SyntaxKind.DeconstructionDeclarationStatement:
                        var localDecl = (DeconstructionDeclarationStatementSyntax)_deconstruction;
                        var localBinder = this.binder.GetBinder(localDecl);
                        localBinder.BindDeconstructionDeclaration(localDecl, localDecl.Assignment.VariableComponent, localDecl.Assignment.Value, diagnostics);
                        break;

                    case SyntaxKind.ForStatement:
                        var forStatement = (ForStatementSyntax)_deconstruction;
                        var forBinder = this.binder.GetBinder(forStatement);
                        forBinder.BindDeconstructionDeclaration(forStatement, forStatement.Deconstruction.VariableComponent, forStatement.Deconstruction.Value, diagnostics);
                        break;

                    case SyntaxKind.ForEachComponentStatement:
                        var foreachBinder = this.binder.GetBinder((ForEachComponentStatementSyntax)_deconstruction);
                        foreachBinder.BindForEachDeconstruction(diagnostics, foreachBinder);
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(_deconstruction.Kind());
                }

                TypeSymbol result = this._type;
                Debug.Assert((object)result != null);
                return result;
            }

            internal override SyntaxNode ForbiddenZone
            {
                get
                {
                    switch (_deconstruction.Kind())
                    {
                        case SyntaxKind.DeconstructionDeclarationStatement:
                            var localDecl = (DeconstructionDeclarationStatementSyntax)_deconstruction;
                            return localDecl.Assignment.Value;

                        case SyntaxKind.ForStatement:
                            var forStatement = (ForStatementSyntax)_deconstruction;
                            return forStatement.Deconstruction;

                        case SyntaxKind.ForEachComponentStatement:
                            // There is no forbidden zone for a foreach statement, because the deconstruction
                            // variables are not in scope in the expression.
                            return null;

                        default:
                            throw ExceptionUtilities.UnexpectedValue(_deconstruction.Kind());
                    }
                }
            }
        }
    }
}
