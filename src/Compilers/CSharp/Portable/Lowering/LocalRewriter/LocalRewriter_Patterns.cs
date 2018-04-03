﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        /// <summary>
        /// A common base class for lowering constructs that use pattern-matching.
        /// </summary>
        private class PatternLocalRewriter
        {
            protected readonly LocalRewriter _localRewriter;
            protected readonly SyntheticBoundNodeFactory _factory;
            protected readonly DagTempAllocator _tempAllocator;

            public PatternLocalRewriter(SyntaxNode node, LocalRewriter localRewriter)
            {
                this._localRewriter = localRewriter;
                this._factory = localRewriter._factory;
                this._tempAllocator = new DagTempAllocator(_factory, node);
            }

            public void Free()
            {
                _tempAllocator.Free();
            }

            protected BoundDagTemp InputTemp(BoundExpression expr) => new BoundDagTemp(expr.Syntax, expr.Type, null, 0);

            public class DagTempAllocator
            {
                private readonly SyntheticBoundNodeFactory _factory;
                private readonly PooledDictionary<BoundDagTemp, BoundExpression> _map = PooledDictionary<BoundDagTemp, BoundExpression>.GetInstance();
                private readonly ArrayBuilder<LocalSymbol> _temps = ArrayBuilder<LocalSymbol>.GetInstance();
                private readonly SyntaxNode _node;

                public DagTempAllocator(SyntheticBoundNodeFactory factory, SyntaxNode node)
                {
                    this._factory = factory;
                    this._node = node;
                }

                public void Free()
                {
                    _temps.Free();
                    _map.Free();
                }

                public BoundExpression GetTemp(BoundDagTemp dagTemp)
                {
                    if (!_map.TryGetValue(dagTemp, out BoundExpression result))
                    {
                        LocalSymbol temp = _factory.SynthesizedLocal(dagTemp.Type, syntax: _node, kind: SynthesizedLocalKind.SwitchCasePatternMatching);
                        result = _factory.Local(temp);
                        _map.Add(dagTemp, result);
                        _temps.Add(temp);
                    }

                    return result;
                }

                /// <summary>
                /// Try setting a user-declared variable (given by its accessing expression) to be
                /// used for a pattern-matching temporary variable. Returns true when not already
                /// assigned. The return value of this method is typically ignored by the caller as
                /// once we have made an assignment we can keep it (we keep the first assignment we
                /// find), but we return a success bool to emphasize that the assignment is not unconditional.
                /// </summary>
                public bool TrySetTemp(BoundDagTemp dagTemp, BoundExpression translation)
                {
                    if (!_map.ContainsKey(dagTemp))
                    {
                        _map.Add(dagTemp, translation);
                        return true;
                    }

                    return false;
                }

                public ImmutableArray<LocalSymbol> AllTemps()
                {
                    return _temps.ToImmutableArray();
                }
            }

            /// <summary>
            /// Return the side-effect expression corresponding to an evaluation.
            /// </summary>
            protected BoundExpression LowerEvaluation(BoundDagEvaluation evaluation)
            {
                BoundExpression input = _tempAllocator.GetTemp(evaluation.Input);
                switch (evaluation)
                {
                    case BoundDagFieldEvaluation f:
                        {
                            FieldSymbol field = f.Field;
                            var outputTemp = new BoundDagTemp(f.Syntax, field.Type, f, index: 0);
                            BoundExpression output = _tempAllocator.GetTemp(outputTemp);
                            BoundExpression access = _localRewriter.MakeFieldAccess(f.Syntax, input, field, null, LookupResultKind.Viable, field.Type);
                            access.WasCompilerGenerated = true;
                            return _factory.AssignmentExpression(output, access);
                        }

                    case BoundDagPropertyEvaluation p:
                        {
                            PropertySymbol property = p.Property;
                            var outputTemp = new BoundDagTemp(p.Syntax, property.Type, p, index: 0);
                            BoundExpression output = _tempAllocator.GetTemp(outputTemp);
                            return _factory.AssignmentExpression(output, _factory.Property(input, property));
                        }

                    case BoundDagDeconstructEvaluation d:
                        {
                            MethodSymbol method = d.DeconstructMethod;
                            var refKindBuilder = ArrayBuilder<RefKind>.GetInstance();
                            var argBuilder = ArrayBuilder<BoundExpression>.GetInstance();
                            BoundExpression receiver;
                            void addArg(RefKind refKind, BoundExpression expression)
                            {
                                refKindBuilder.Add(refKind);
                                argBuilder.Add(expression);
                            }

                            Debug.Assert(method.Name == "Deconstruct");
                            int extensionExtra;
                            if (method.IsStatic)
                            {
                                Debug.Assert(method.IsExtensionMethod);
                                receiver = _factory.Type(method.ContainingType);
                                addArg(method.ParameterRefKinds[0], input);
                                extensionExtra = 1;
                            }
                            else
                            {
                                receiver = input;
                                extensionExtra = 0;
                            }

                            for (int i = extensionExtra; i < method.ParameterCount; i++)
                            {
                                ParameterSymbol parameter = method.Parameters[i];
                                Debug.Assert(parameter.RefKind == RefKind.Out);
                                var outputTemp = new BoundDagTemp(d.Syntax, parameter.Type, d, i - extensionExtra);
                                addArg(RefKind.Out, _tempAllocator.GetTemp(outputTemp));
                            }

                            return _factory.Call(receiver, method, refKindBuilder.ToImmutableAndFree(), argBuilder.ToImmutableAndFree());
                        }

                    case BoundDagTypeEvaluation t:
                        {
                            TypeSymbol inputType = input.Type;
                            if (inputType.IsDynamic())
                            {
                                inputType = _factory.SpecialType(SpecialType.System_Object);
                            }

                            TypeSymbol type = t.Type;
                            var outputTemp = new BoundDagTemp(t.Syntax, type, t, index: 0);
                            BoundExpression output = _tempAllocator.GetTemp(outputTemp);
                            HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                            Conversion conversion = _factory.Compilation.Conversions.ClassifyBuiltInConversion(inputType, output.Type, ref useSiteDiagnostics);
                            _localRewriter._diagnostics.Add(t.Syntax, useSiteDiagnostics);
                            BoundExpression evaluated;
                            if (conversion.Exists)
                            {
                                if (conversion.Kind == ConversionKind.ExplicitNullable &&
                                    inputType.GetNullableUnderlyingType().Equals(output.Type, TypeCompareKind.AllIgnoreOptions) &&
                                    _localRewriter.TryGetNullableMethod(t.Syntax, inputType, SpecialMember.System_Nullable_T_GetValueOrDefault, out MethodSymbol getValueOrDefault))
                                {
                                    // As a special case, since the null test has already been done we can use Nullable<T>.GetValueOrDefault
                                    evaluated = _factory.Call(input, getValueOrDefault);
                                }
                                else
                                {
                                    evaluated = _factory.Convert(type, input, conversion);
                                }
                            }
                            else
                            {
                                evaluated = _factory.As(input, type);
                            }

                            return _factory.AssignmentExpression(output, evaluated);
                        }

                    default:
                        throw ExceptionUtilities.UnexpectedValue(evaluation);
                }
            }

            /// <summary>
            /// Return the boolean expression to be evaluated for the given test. Returns `null` if the test is trivially true.
            /// </summary>
            protected BoundExpression LowerTest(BoundDagTest test)
            {
                _factory.Syntax = test.Syntax;
                BoundExpression input = _tempAllocator.GetTemp(test.Input);
                switch (test)
                {
                    case BoundDagNonNullTest d:
                        return _localRewriter.MakeNullCheck(d.Syntax, input, input.Type.IsNullableType() ? BinaryOperatorKind.NullableNullNotEqual : BinaryOperatorKind.NotEqual);

                    case BoundDagTypeTest d:
                        // Note that this tests for non-null as a side-effect. We depend on that to sometimes avoid the null check.
                        return _factory.Is(input, d.Type);

                    case BoundDagNullTest d:
                        return _localRewriter.MakeNullCheck(d.Syntax, input, input.Type.IsNullableType() ? BinaryOperatorKind.NullableNullEqual : BinaryOperatorKind.Equal);

                    case BoundDagValueTest d:
                        Debug.Assert(!input.Type.IsNullableType());
                        return MakeEqual(_localRewriter.MakeLiteral(d.Syntax, d.Value, input.Type), input);

                    default:
                        throw ExceptionUtilities.UnexpectedValue(test);
                }
            }

            private BoundExpression MakeEqual(BoundExpression loweredLiteral, BoundExpression input)
            {
                Debug.Assert(loweredLiteral.Type == input.Type);

                if (loweredLiteral.Type.SpecialType == SpecialType.System_Double && double.IsNaN(loweredLiteral.ConstantValue.DoubleValue))
                {
                    // produce double.IsNaN(input)
                    return _factory.StaticCall(SpecialMember.System_Double__IsNaN, input);
                }
                else if (loweredLiteral.Type.SpecialType == SpecialType.System_Single && float.IsNaN(loweredLiteral.ConstantValue.SingleValue))
                {
                    // produce float.IsNaN(input)
                    return _factory.StaticCall(SpecialMember.System_Single__IsNaN, input);
                }

                NamedTypeSymbol booleanType = _factory.SpecialType(SpecialType.System_Boolean);
                NamedTypeSymbol intType = _factory.SpecialType(SpecialType.System_Int32);
                switch (loweredLiteral.Type.SpecialType)
                {
                    case SpecialType.System_Boolean:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.BoolEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_Byte:
                    case SpecialType.System_Char:
                    case SpecialType.System_Int16:
                    case SpecialType.System_SByte:
                    case SpecialType.System_UInt16:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.IntEqual, _factory.Convert(intType, loweredLiteral), _factory.Convert(intType, input), booleanType, method: null);
                    case SpecialType.System_Decimal:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.DecimalEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_Double:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.DoubleEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_Int32:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.IntEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_Int64:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.LongEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_Single:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.FloatEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_String:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.StringEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_UInt32:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.UIntEqual, loweredLiteral, input, booleanType, method: null);
                    case SpecialType.System_UInt64:
                        return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.ULongEqual, loweredLiteral, input, booleanType, method: null);
                    default:
                        if (loweredLiteral.Type.IsEnumType())
                        {
                            return _localRewriter.MakeBinaryOperator(_factory.Syntax, BinaryOperatorKind.EnumEqual, loweredLiteral, input, booleanType, method: null);
                        }

                        // This is the (correct but inefficient) fallback for any type that isn't yet implemented.
                        // However, the above should handle all types.
                        Debug.Assert(false); // don't fail in non-debug builds
                        NamedTypeSymbol systemObject = _factory.SpecialType(SpecialType.System_Object);
                        return _factory.StaticCall(
                            systemObject,
                            "Equals",
                            _factory.Convert(systemObject, loweredLiteral),
                            _factory.Convert(systemObject, input)
                            );
                }
            }

            /// <summary>
            /// Lower a test followed by an evaluation into a side-effect followed by a test. This permits us to optimize
            /// a type test followed by a cast into an `as` expression followed by a null check. Returns true if the optimization
            /// applies and the results are placed into <paramref name="sideEffect"/> and <paramref name="test"/>. The caller
            /// should place the side-effect before the test in the generated code.
            /// </summary>
            /// <param name="evaluation"></param>
            /// <param name="test"></param>
            /// <param name="sideEffect"></param>
            /// <param name="testExpression"></param>
            /// <returns>true if the optimization is applied</returns>
            protected bool TryLowerTypeTestAndCast(
                BoundDagTest test,
                BoundDagEvaluation evaluation,
                out BoundExpression sideEffect,
                out BoundExpression testExpression)
            {
                if (test is BoundDagTypeTest typeDecision &&
                    evaluation is BoundDagTypeEvaluation typeEvaluation &&
                    typeDecision.Type.IsReferenceType &&
                    typeDecision.Input.Type.IsReferenceType &&
                    typeEvaluation.Type == typeDecision.Type &&
                    typeEvaluation.Input == typeDecision.Input
                    )
                {
                    BoundExpression input = _tempAllocator.GetTemp(test.Input);
                    BoundExpression output = _tempAllocator.GetTemp(new BoundDagTemp(evaluation.Syntax, typeEvaluation.Type, evaluation, index: 0));
                    sideEffect = _factory.AssignmentExpression(output, _factory.As(input, typeEvaluation.Type));
                    testExpression = _factory.ObjectNotEqual(output, _factory.Null(output.Type));
                    return true;
                }

                sideEffect = testExpression = null;
                return false;
            }

            /// <summary>
            /// Produce assignment of the input expression. This method is also responsible for assigning
            /// variables for some pattern-matching temps that can be shared with user variables.
            /// </summary>
            protected BoundDecisionDag ShareTempsAndEvaluateInput(
                BoundExpression loweredInput,
                BoundDecisionDag decisionDag,
                Action<BoundExpression> addCode)
            {
                var inputDagTemp = InputTemp(loweredInput);
                if (loweredInput.Kind == BoundKind.Local || loweredInput.Kind == BoundKind.Parameter)
                {
                    // If we're switching on a local variable and there is no when clause (checked by the caller),
                    // we assume the value of the local variable does not change during the execution of the
                    // decision automaton and we just reuse the local variable when we need the input expression.
                    // It is possible for this assumption to be violated by a side-effecting Deconstruct that
                    // modifies the local variable which has been captured in a lambda. Since the language assumes
                    // that functions called by pattern-matching are idempotent and not side-effecting, we feel
                    // justified in taking this assumption in the compiler too.
                    bool tempAssigned = _tempAllocator.TrySetTemp(inputDagTemp, loweredInput);
                    Debug.Assert(tempAssigned);
                }

                foreach (BoundDecisionDagNode node in decisionDag.TopologicallySortedNodes)
                {
                    if (node is BoundWhenDecisionDagNode w)
                    {
                        // We share a slot for a user-declared pattern-matching variable with a pattern temp if there
                        // is no user-written when-clause that could modify the variable before the matching
                        // automaton is done with it (checked by the caller).
                        foreach (BoundPatternBinding binding in w.Bindings)
                        {
                            if (binding.VariableAccess is BoundLocal l)
                            {
                                Debug.Assert(l.LocalSymbol.DeclarationKind == LocalDeclarationKind.PatternVariable);
                                _ = _tempAllocator.TrySetTemp(binding.TempContainingValue, binding.VariableAccess);
                            }
                        }
                    }
                }

                if (loweredInput.Kind == BoundKind.TupleLiteral &&
                    !decisionDag.TopologicallySortedNodes.Any(n => !usesOriginalInput(n)) &&
                    false)
                {
                    // If the switch governing expression is a tuple literal that is not used anywhere,
                    // (though perhaps its component parts are used), then we can save the component parts
                    // and assign them into temps (or perhaps user variables) to avoid the creation of
                    // the tuple altogether.
                    decisionDag = RewriteTupleSwitch(decisionDag, (BoundTupleLiteral)loweredInput, addCode);
                }
                else
                {
                    // Otherwise we emit an assignment of the input expression to a temporary variable.
                    BoundExpression inputTemp = _tempAllocator.GetTemp(inputDagTemp);
                    if (inputTemp != loweredInput)
                    {
                        addCode(_factory.AssignmentExpression(inputTemp, loweredInput));
                    }
                }

                return decisionDag;

                bool usesOriginalInput(BoundDecisionDagNode node)
                {
                    switch (node)
                    {
                        case BoundWhenDecisionDagNode n:
                            return (n.Bindings.Any(b => b.TempContainingValue.IsOriginalInput));
                        case BoundTestDecisionDagNode t:
                            return t.Test.Input.IsOriginalInput;
                        case BoundEvaluationDecisionDagNode e:
                            switch (e.Evaluation)
                            {
                                case BoundDagFieldEvaluation f:
                                    return f.Input.IsOriginalInput && !f.Field.IsTupleElement();
                                default:
                                    return e.Evaluation.Input.IsOriginalInput;
                            }
                        default:
                            return false;
                    }
                }
            }

            /// <summary>
            /// We have a decision dag whose input is a tuple literal, and the decision dag does not need the tuple itself.
            /// We rewrite the decision dag into one which doesn't touch the tuple, but instead works directly with the
            /// values that have been stored in temps. This permits the caller to avoid creation of the tuple object
            /// itself. We also emit assignments of the tuple values into their corresponding temps.
            /// </summary>
            /// <returns>A new decision dag that does not reference the input directly</returns>
            private BoundDecisionDag RewriteTupleSwitch(
                BoundDecisionDag decisionDag,
                BoundTupleLiteral loweredInput,
                Action<BoundExpression> addCode)
            {
                throw new NotImplementedException();
            }
        }
    }
}
