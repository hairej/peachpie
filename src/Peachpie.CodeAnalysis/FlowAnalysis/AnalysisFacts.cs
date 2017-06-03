﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Devsense.PHP.Syntax;
using Microsoft.CodeAnalysis;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Symbols;

namespace Pchp.CodeAnalysis.FlowAnalysis
{
    static class AnalysisFacts
    {
        /// <summary>
        /// Resolves value of the function call in compile time if possible and updates the variable type if necessary
        /// </summary>
        public static void HandleFunctionCall(BoundGlobalFunctionCall call, ExpressionAnalysis analysis, ConditionBranch branch)
        {
            // Only direct function names
            if (!HasSimpleName(call, out string name))
            {
                return;
            }

            // Type checking functions
            if (branch != ConditionBranch.AnyResult && CanBeTypeCheckingFunction(call, name, out var arg))
            {
                if (HandleTypeCheckingFunctions(call, name, arg, analysis, branch))
                {
                    return;
                }
            }

            // Functions with all arguments resolved
            if (call.ArgumentsInSourceOrder.All(a => a.Value.ConstantValue.HasValue))
            {
                // Clear out the constant value result from the previous run of this method (if it was valid, it will be reassigned below)
                call.ConstantValue = default(Optional<object>);

                var args = call.ArgumentsInSourceOrder;
                switch (name)
                {
                    case "function_exists":
                        if (args.Length == 1)
                        {
                            // TRUE <=> function name is defined unconditionally in a reference library (PE assembly)
                            var str = args[0].Value.ConstantValue.Value as string;
                            if (str != null)
                            {
                                var tmp = analysis.Model.ResolveFunction(NameUtils.MakeQualifiedName(str, true));
                                if (tmp is PEMethodSymbol || (tmp is AmbiguousMethodSymbol && ((AmbiguousMethodSymbol)tmp).Ambiguities.All(f => f is PEMethodSymbol)))
                                {
                                    call.ConstantValue = new Optional<object>(true);
                                    return;
                                }
                            }
                        }
                        break;
                }
            }
        }

        private static bool HasSimpleName(BoundGlobalFunctionCall call, out string name)
        {
            if (call.Name.IsDirect)
            {
                // Take the function name ignoring current namespace resolution, simple names only:
                var qualifiedName = call.NameOpt.HasValue ? call.NameOpt.Value : call.Name.NameValue;
                if (qualifiedName.IsSimpleName)
                {
                    name = qualifiedName.Name.Value;
                    return true;
                }
            }

            name = null;
            return false;
        }

        private static bool CanBeTypeCheckingFunction(BoundGlobalFunctionCall call, string name, out BoundVariableRef arg)
        {
            if (name.StartsWith("is_") && call.ArgumentsInSourceOrder.Length == 1
                && call.ArgumentsInSourceOrder[0].Value is BoundVariableRef onlyArg)
            {
                arg = onlyArg;
                return true;
            }
            else
            {
                arg = null;
                return false;
            }
        }

        /// <summary>
        /// Processes functions such as is_int, is_bool etc. Returns whether the function was one of these.
        /// </summary>
        private static bool HandleTypeCheckingFunctions(
            BoundGlobalFunctionCall call,
            string name,
            BoundVariableRef arg,
            ExpressionAnalysis analysis,
            ConditionBranch branch)
        {
            var typeCtx = analysis.TypeCtx;
            var flowState = analysis.State;

            switch (name)
            {
                case "is_int":
                case "is_integer":
                case "is_long":
                    HandleTypeCheckingExpression(arg, typeCtx.GetLongTypeMask(), branch, flowState, checkExpr: call);
                    return true;

                case "is_bool":
                    HandleTypeCheckingExpression(arg, typeCtx.GetBooleanTypeMask(), branch, flowState, checkExpr: call);
                    return true;

                case "is_float":
                case "is_double":
                case "is_real":
                    HandleTypeCheckingExpression(arg, typeCtx.GetDoubleTypeMask(), branch, flowState, checkExpr: call);
                    return true;

                case "is_string":
                    var stringMask = typeCtx.GetStringTypeMask() | typeCtx.GetWritableStringTypeMask();
                    HandleTypeCheckingExpression(arg, stringMask, branch, flowState, checkExpr: call);
                    return true;

                case "is_resource":
                    HandleTypeCheckingExpression(arg, typeCtx.GetResourceTypeMask(), branch, flowState, checkExpr: call);
                    return true;

                case "is_null":
                    HandleTypeCheckingExpression(arg, typeCtx.GetNullTypeMask(), branch, flowState, checkExpr: call);
                    return true;

                case "is_array":
                    HandleTypeCheckingExpression(
                        arg,
                        currentType => typeCtx.GetArraysFromMask(currentType),
                        branch,
                        flowState,
                        skipPositiveIfAnyType: true,
                        checkExpr: call);
                    return true;

                case "is_object":
                    // Keep IncludesSubclasses flag in the true branch and clear it in the false branch
                    HandleTypeCheckingExpression(
                        arg,
                        currentType => typeCtx.GetObjectsFromMask(currentType).WithIncludesSubclasses,
                        branch,
                        flowState,
                        skipPositiveIfAnyType: true,
                        checkExpr: call);
                    return true;

                // TODO
                //case "is_scalar":
                //    return;

                case "is_numeric":
                    HandleTypeCheckingExpression(
                        arg,
                        currentType =>
                        {
                            // Specify numeric types if they are present 
                            var targetType = typeCtx.IsLong(currentType) ? typeCtx.GetLongTypeMask() : 0;
                            targetType |= typeCtx.IsDouble(currentType) ? typeCtx.GetDoubleTypeMask() : 0;

                            if (branch == ConditionBranch.ToTrue)
                            {
                                // Also string types can make is_numeric return true, but not anything else
                                targetType |= typeCtx.IsReadonlyString(currentType) ? typeCtx.GetStringTypeMask() : 0;
                                targetType |= typeCtx.IsWritableString(currentType) ? typeCtx.GetWritableStringTypeMask() : 0;

                                return targetType;
                            }
                            else
                            {
                                // For number, is_numeric always returns true -> remove numeric types from false branch
                                return targetType;
                            }
                        },
                        branch,
                        flowState,
                        skipPositiveIfAnyType: true,
                        checkExpr: call);
                    return true;

                case "is_callable":
                    HandleTypeCheckingExpression(
                        arg,
                        currentType =>
                        {
                            // Closure is specified in both branches
                            TypeRefMask targetType = 0;
                            AddTypeIfInContext(typeCtx, NameUtils.SpecialNames.Closure, false, ref targetType);

                            if (branch == ConditionBranch.ToTrue)
                            {
                                // Also string types, arrays and object can make is_callable return true, but not anything else
                                targetType |= typeCtx.IsReadonlyString(currentType) ? typeCtx.GetStringTypeMask() : 0;
                                targetType |= typeCtx.IsWritableString(currentType) ? typeCtx.GetWritableStringTypeMask() : 0;
                                targetType |= typeCtx.GetArraysFromMask(currentType);
                                AddTypeIfInContext(typeCtx, NameUtils.SpecialNames.System_Object, true, ref targetType);

                                return targetType;
                            }
                            else
                            {
                                // For closure, is_callable always returns true -> remove the closure type from false branch,
                                // don't remove IncludeSubclasses flag
                                return targetType;
                            }
                        },
                        branch,
                        flowState,
                        skipPositiveIfAnyType: true,
                        checkExpr: call);
                    return true;

                // TODO
                //case "is_iterable":
                //    return;

                default:
                    return false;
            }
        }

        private static void AddTypeIfInContext(TypeRefContext typeCtx, QualifiedName name, bool includeSubclasses, ref TypeRefMask mask)
        {
            var closureTypeRef = typeCtx.Types.FirstOrDefault(t => t.QualifiedName == name);
            if (closureTypeRef != null)
            {
                mask |= typeCtx.GetTypeMask(closureTypeRef, includeSubclasses);
            }
        }

        /// <summary>
        /// Ensures that the variable is of the given type(s) in the positive branch or not of this type in the negative
        /// branch. If the current branch is unfeasible, assigns an appropriate boolean to the
        /// <see cref="BoundExpression.ConstantValue"/> of <paramref name="checkExpr"/>.
        /// </summary>
        /// <param name="varRef">The reference to the variable whose types to check.</param>
        /// <param name="targetType">The target type of the variable.</param>
        /// <param name="branch">The branch to check - <see cref="ConditionBranch.ToTrue"/> is understood as the positive
        /// branch if <paramref name="isPositiveCheck"/> is true.</param>
        /// <param name="flowState">The flow state of the branch.</param>
        /// <param name="skipPositiveIfAnyType">Whether to skip a mask with <see cref="TypeRefMask.IsAnyType"/> in the
        /// positive branch (in the negative branch, it is always skipped).</param>
        /// <param name="checkExpr">The expression to have its <see cref="BoundExpression.ConstantValue"/> potentially
        /// updated.</param>
        /// <param name="isPositiveCheck">Whether the expression returns true when the type check succeeds. For example,
        /// in the case of != it would be false.</param>
        public static void HandleTypeCheckingExpression(
            BoundVariableRef varRef,
            TypeRefMask targetType,
            ConditionBranch branch,
            FlowState flowState,
            bool skipPositiveIfAnyType = false,
            BoundExpression checkExpr = null,
            bool isPositiveCheck = true)
        {
            HandleTypeCheckingExpression(varRef, (var) => targetType, branch, flowState, skipPositiveIfAnyType, checkExpr, isPositiveCheck);
        }

        /// <summary>
        /// Ensures that the variable is of the given type(s) in the positive branch or not of this type in the negative
        /// branch. If the current branch is unfeasible, assigns an appropriate boolean to the
        /// <see cref="BoundExpression.ConstantValue"/> of <paramref name="checkExpr"/>.
        /// </summary>
        /// <param name="varRef">The reference to the variable whose types to check.</param>
        /// <param name="targetTypeCallback">The callback that receives the current type mask of the variable and returns
        /// the target one.</param>
        /// <param name="branch">The branch to check - <see cref="ConditionBranch.ToTrue"/> is understood as the positive
        /// branch if <paramref name="isPositiveCheck"/> is true.</param>
        /// <param name="flowState">The flow state of the branch.</param>
        /// <param name="skipPositiveIfAnyType">Whether to skip a mask with <see cref="TypeRefMask.IsAnyType"/> in the
        /// positive branch (in the negative branch, it is always skipped).</param>
        /// <param name="checkExpr">The expression to have its <see cref="BoundExpression.ConstantValue"/> potentially
        /// updated.</param>
        /// <param name="isPositiveCheck">Whether the expression returns true when the type check succeeds. For example,
        /// in the case of != it would be false.</param>
        public static void HandleTypeCheckingExpression(
            BoundVariableRef varRef,
            Func<TypeRefMask, TypeRefMask> targetTypeCallback,
            ConditionBranch branch,
            FlowState flowState,
            bool skipPositiveIfAnyType = false,
            BoundExpression checkExpr = null,
            bool isPositiveCheck = true)
        {
            if (!TryGetVariableHandle(varRef, flowState, out VariableHandle handle))
            {
                return;
            }

            var currentType = flowState.GetLocalType(handle);
            var targetType = targetTypeCallback(currentType);

            // Model negative type checks (such as $x != null) by inverting branches for the core checking function
            var branchHlp = isPositiveCheck ? branch : branch.NegativeBranch();

            bool isFeasible = HandleTypeChecking(currentType, targetType, branchHlp, flowState, handle, skipPositiveIfAnyType);

            // If the true branch proves to be unfeasible, the function always returns false and vice versa
            var resultConstVal = isFeasible ? default(Optional<object>) : new Optional<object>(!branch.ToBool().Value);

            // Each branch can clean only the constant value it produced during its analysis (in order not to lose result
            // of the other branch): true branch can produce false value and vice versa
            if (checkExpr != null && !resultConstVal.EqualsOptional(checkExpr.ConstantValue)
                && (resultConstVal.HasValue
                    || checkExpr.ConstantValue.Value is false && branch == ConditionBranch.ToTrue
                    || checkExpr.ConstantValue.Value is true && branch == ConditionBranch.ToFalse))
            {
                checkExpr.ConstantValue = resultConstVal;
            }
        }

        private static bool TryGetVariableHandle(BoundVariableRef varRef, FlowState state, out VariableHandle varHandle)
        {
            if (varRef.Variable.VariableKind == VariableKind.LocalVariable || varRef.Variable.VariableKind == VariableKind.Parameter)
            {
                varHandle = state.GetLocalHandle(varRef.Variable.Name);
                return true;
            }
            else
            {
                varHandle = default(VariableHandle);
                return false;
            }
        }

        private static bool HandleTypeChecking(
            TypeRefMask currentType,
            TypeRefMask targetType,
            ConditionBranch branch,
            FlowState flowState,
            VariableHandle handle,
            bool skipTrueIfAnyType)
        {
            // Information whether this path can ever be taken
            bool isFeasible = true;

            if (branch == ConditionBranch.ToTrue)
            {
                // In the true branch the IsAnyType case can be optionally skipped
                if (skipTrueIfAnyType && currentType.IsAnyType)
                {
                    return isFeasible;
                }

                // Intersect the possible types with those checked by the function, always keeping the IsRef flag.
                // IncludesSubclasses is kept only if it is specified in targetType.
                TypeRefMask resultType = (currentType & (targetType | TypeRefMask.IsRefMask));

                if (resultType.IsVoid)
                {
                    // Clearing the type out in this branch means the variable will never be of that type.
                    // In order to prevent errors in analysis and code generation, set the type to the one specified.
                    resultType = targetType | (currentType & TypeRefMask.IsRefMask);

                    isFeasible = false;
                }

                flowState.SetLocalType(handle, resultType);
            }
            else
            {
                Debug.Assert(branch == ConditionBranch.ToFalse);

                // In the false branch we cannot handle the IsAnyType case
                if (currentType.IsAnyType)
                {
                    return isFeasible;
                }

                // Remove the types and flags excluded by the fact that the function returned false
                TypeRefMask resultType = currentType & (~targetType);

                if (resultType.IsVoid)
                {
                    // Clearing the type out in this branch means the variable will always be of that type
                    // In order to prevent errors in analysis and code generation, do not alter the type in this case.

                    isFeasible = false;
                }
                else
                {
                    flowState.SetLocalType(handle, resultType);
                }
            }

            return isFeasible;
        }
    }
}