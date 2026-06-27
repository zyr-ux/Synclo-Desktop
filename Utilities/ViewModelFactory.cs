using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Synclo.ViewModels;

namespace Synclo.Utilities
{
    public interface IViewModelFactory
    {
        T Create<T>(params object?[] args) where T : ViewModelBase;
        void Release(ViewModelBase? viewModel);
    }

    public sealed class ViewModelFactory(IServiceProvider rootProvider) : IViewModelFactory
    {
        private readonly ConcurrentDictionary<ViewModelBase, IServiceScope> _scopes = new();

        public T Create<T>(params object?[] args)
            where T : ViewModelBase
        {
            var scope = rootProvider.CreateScope();

            try
            {
                var vm = ActivatorUtilities.CreateInstance<T>(
                    scope.ServiceProvider,
                    (object[])args!)!;

                _scopes.TryAdd(vm, scope);

                return vm;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public void Release(ViewModelBase? viewModel)
        {
            if (viewModel is null)
                return;

            if (_scopes.TryRemove(viewModel, out var scope))
            {
                try
                {
                    if (viewModel is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }

                    try
                    {
                        scope.Dispose();
                    }
                    catch
                    {
                        
                    }
                }
                catch
                {
                    // Never throw during cleanup.
                }
            }
            else if (viewModel is IDisposable disposable)
            {
                // Fallback for unmanaged/manual ViewModels not created by this factory.
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    
                }
            }
        }
    }
}
