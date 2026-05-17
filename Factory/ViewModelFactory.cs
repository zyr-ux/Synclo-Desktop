using System;
using Microsoft.Extensions.DependencyInjection;
using Synclo.ViewModels;

namespace Synclo.Factory
{
    public interface IViewModelFactory
    {
        T Create<T>() where T : ViewModelBase;
        T Create<T, TArg>(TArg arg) where T : ViewModelBase;
        T Create<T, TArg1, TArg2>(TArg1 arg1, TArg2 arg2) where T : ViewModelBase;
    }


    public sealed class ViewModelFactory(IServiceProvider services) : IViewModelFactory
    {
        public T Create<T>() where T : ViewModelBase
            => services.GetRequiredService<T>();

        public T Create<T, TArg>(TArg arg) where T : ViewModelBase
            => ActivatorUtilities.CreateInstance<T>(services, arg!);

        public T Create<T, TArg1, TArg2>(TArg1 arg1, TArg2 arg2)
            where T : ViewModelBase
            => ActivatorUtilities.CreateInstance<T>(services, arg1!, arg2!);
    }
}
