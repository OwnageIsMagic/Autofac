﻿// Copyright (c) Autofac Project. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Autofac.Core.Resolving;
using Autofac.Core.Resolving.Pipeline;

namespace Autofac.Core.Activators.Reflection;

/// <summary>
/// Uses reflection to activate instances of a type.
/// </summary>
[SuppressMessage("Microsoft.Design", "CA1063:ImplementIDisposableCorrectly", Justification = "There is nothing in the derived class to dispose so no override is necessary.")]
public class ReflectionActivator : InstanceActivator, IInstanceActivator
{
    private readonly Type _implementationType;
    private readonly Parameter[] _configuredProperties;
    private readonly Parameter[] _defaultParameters;

    private ConstructorBinder[]? _constructorBinders;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReflectionActivator"/> class.
    /// </summary>
    /// <param name="implementationType">Type to activate.</param>
    /// <param name="constructorFinder">Constructor finder.</param>
    /// <param name="constructorSelector">Constructor selector.</param>
    /// <param name="configuredParameters">Parameters configured explicitly for this instance.</param>
    /// <param name="configuredProperties">Properties configured explicitly for this instance.</param>
    public ReflectionActivator(
        Type implementationType,
        IConstructorFinder constructorFinder,
        IConstructorSelector constructorSelector,
        IEnumerable<Parameter> configuredParameters,
        IEnumerable<Parameter> configuredProperties)
        : base(implementationType)
    {
        if (configuredParameters == null)
        {
            throw new ArgumentNullException(nameof(configuredParameters));
        }

        if (configuredProperties == null)
        {
            throw new ArgumentNullException(nameof(configuredProperties));
        }

        _implementationType = implementationType;
        ConstructorFinder = constructorFinder ?? throw new ArgumentNullException(nameof(constructorFinder));
        ConstructorSelector = constructorSelector ?? throw new ArgumentNullException(nameof(constructorSelector));
        _configuredProperties = configuredProperties.ToArray();
        _defaultParameters = configuredParameters.Concat(new Parameter[] { new AutowiringParameter(), new DefaultValueParameter() }).ToArray();
    }

    /// <summary>
    /// Gets the constructor finder.
    /// </summary>
    public IConstructorFinder ConstructorFinder { get; }

    /// <summary>
    /// Gets the constructor selector.
    /// </summary>
    public IConstructorSelector ConstructorSelector { get; }

    /// <inheritdoc/>
    public void ConfigurePipeline(IComponentRegistryServices componentRegistryServices, IResolvePipelineBuilder pipelineBuilder)
    {
        if (componentRegistryServices is null)
        {
            throw new ArgumentNullException(nameof(componentRegistryServices));
        }

        if (pipelineBuilder is null)
        {
            throw new ArgumentNullException(nameof(pipelineBuilder));
        }

        // Locate the possible constructors at container build time.
        var availableConstructors = ConstructorFinder.FindConstructors(_implementationType);

        if (availableConstructors.Length == 0)
        {
            throw new NoConstructorsFoundException(_implementationType, string.Format(CultureInfo.CurrentCulture, ReflectionActivatorResources.NoConstructorsAvailable, _implementationType, ConstructorFinder));
        }

        var binders = new ConstructorBinder[availableConstructors.Length];

        for (var idx = 0; idx < availableConstructors.Length; idx++)
        {
            binders[idx] = new ConstructorBinder(availableConstructors[idx]);
        }

        if (binders.Length == 1)
        {
            UseSingleConstructorActivation(pipelineBuilder, binders[0]);
            return;
        }
        else if (ConstructorSelector is IConstructorSelectorWithEarlyBinding earlyBindingSelector)
        {
            var matchedConstructor = earlyBindingSelector.SelectConstructorBinder(binders);

            if (matchedConstructor is not null)
            {
                UseSingleConstructorActivation(pipelineBuilder, matchedConstructor);

                return;
            }
        }

        _constructorBinders = binders;

        pipelineBuilder.Use(ToString(), PipelinePhase.Activation, MiddlewareInsertionMode.EndOfPhase, (ctxt, next) =>
        {
            ctxt.Instance = ActivateInstance(ctxt, ctxt.Parameters);

            next(ctxt);
        });
    }

    private void UseSingleConstructorActivation(IResolvePipelineBuilder pipelineBuilder, ConstructorBinder singleConstructor)
    {
        if (singleConstructor.ParameterCount == 0)
        {
            var constructorInvoker = singleConstructor.GetConstructorInvoker();

            if (constructorInvoker is null)
            {
                // This is not going to happen, because there is only 1 constructor, that constructor has no parameters,
                // so there are no conditions under which GetConstructorInvoker will return null in this path.
                // Throw an error here just in case (and to satisfy nullability checks).
                throw new NoConstructorsFoundException(_implementationType, string.Format(CultureInfo.CurrentCulture, ReflectionActivatorResources.NoConstructorsAvailable, _implementationType, ConstructorFinder));
            }

            // If there are no arguments to the constructor, bypass all argument binding and pre-bind the constructor.
            var boundConstructor = BoundConstructor.ForBindSuccess(
                singleConstructor,
                constructorInvoker,
                Array.Empty<Func<object?>>());

            // Fast-path to just create an instance.
            pipelineBuilder.Use(ToString(), PipelinePhase.Activation, MiddlewareInsertionMode.EndOfPhase, (ctxt, next) =>
            {
                CheckNotDisposed();

                var instance = boundConstructor.Instantiate();

                InjectProperties(instance, ctxt);

                ctxt.Instance = instance;

                next(ctxt);
            });
        }
        else
        {
            pipelineBuilder.Use(ToString(), PipelinePhase.Activation, MiddlewareInsertionMode.EndOfPhase, (ctxt, next) =>
            {
                CheckNotDisposed();

                var prioritisedParameters = GetAllParameters(ctxt.Parameters);

                var bound = singleConstructor.Bind(prioritisedParameters, ctxt);

                if (!bound.CanInstantiate)
                {
                    throw new DependencyResolutionException(GetBindingFailureMessage(new[] { bound }));
                }

                var instance = bound.Instantiate();

                InjectProperties(instance, ctxt);

                ctxt.Instance = instance;

                next(ctxt);
            });
        }
    }

    /// <summary>
    /// Activate an instance in the provided context.
    /// </summary>
    /// <param name="context">Context in which to activate instances.</param>
    /// <param name="parameters">Parameters to the instance.</param>
    /// <returns>The activated instance.</returns>
    /// <remarks>
    /// The context parameter here should probably be ILifetimeScope in order to reveal Disposer,
    /// but will wait until implementing a concrete use case to make the decision.
    /// </remarks>
    private object ActivateInstance(IComponentContext context, IEnumerable<Parameter> parameters)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        CheckNotDisposed();

        var allBindings = GetAllBindings(_constructorBinders!, context, parameters);

        var selectedBinding = ConstructorSelector.SelectConstructorBinding(allBindings, parameters);

        if (!selectedBinding.CanInstantiate)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                ReflectionActivatorResources.ConstructorSelectorCannotSelectAnInvalidBinding,
                ConstructorSelector.GetType().Name));
        }

        var instance = selectedBinding.Instantiate();

        InjectProperties(instance, context);

        return instance;
    }

    private BoundConstructor[] GetAllBindings(ConstructorBinder[] availableConstructors, IComponentContext context, IEnumerable<Parameter> parameters)
    {
        var prioritisedParameters = GetAllParameters(parameters);

        var boundConstructors = new BoundConstructor[availableConstructors.Length];
        var validBindings = availableConstructors.Length;

        for (var idx = 0; idx < availableConstructors.Length; idx++)
        {
            var bound = availableConstructors[idx].Bind(prioritisedParameters, context);

            boundConstructors[idx] = bound;

            if (!bound.CanInstantiate)
            {
                validBindings--;
            }
        }

        if (validBindings == 0)
        {
            throw new DependencyResolutionException(GetBindingFailureMessage(boundConstructors));
        }

        return boundConstructors;
    }

    private IEnumerable<Parameter> GetAllParameters(IEnumerable<Parameter> parameters)
    {
        // Most often, there will be no `parameters` and/or no `_defaultParameters`; in both of those cases we can avoid allocating.
        // Do a reference compare with the NoParameters constant; faster than an Any() check for the common case.
        if (ReferenceEquals(ResolveRequest.NoParameters, parameters))
        {
            return _defaultParameters;
        }

        if (parameters.Any())
        {
            return EnumerateParameters(parameters);
        }

        return _defaultParameters;
    }

    private IEnumerable<Parameter> EnumerateParameters(IEnumerable<Parameter> parameters)
    {
        foreach (var param in parameters)
        {
            yield return param;
        }

        foreach (var defaultParam in _defaultParameters)
        {
            yield return defaultParam;
        }
    }

    private string GetBindingFailureMessage(BoundConstructor[] constructorBindings)
    {
        var reasons = new StringBuilder();

        foreach (var invalid in constructorBindings.Where(cb => !cb.CanInstantiate))
        {
            reasons.AppendLine();
            reasons.Append(invalid.Description);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            ReflectionActivatorResources.NoConstructorsBindable,
            ConstructorFinder,
            _implementationType,
            reasons);
    }

    private void InjectProperties(object instance, IComponentContext context)
    {
        if (_configuredProperties.Length == 0)
        {
            return;
        }

        var actualProperties = instance
            .GetType()
            .GetRuntimeProperties()
            .Where(pi => pi.CanWrite)
            .ToList();

        foreach (var configuredProperty in _configuredProperties)
        {
            foreach (var actualProperty in actualProperties)
            {
                var setter = actualProperty.SetMethod;

                if (setter != null &&
                    configuredProperty.CanSupplyValue(setter.GetParameters()[0], context, out var vp))
                {
                    actualProperties.Remove(actualProperty);
                    actualProperty.SetValue(instance, vp(), null);
                    break;
                }
            }
        }
    }
}
