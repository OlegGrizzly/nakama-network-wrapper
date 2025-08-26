using System.Collections;
using UnityEngine;

namespace OlegGrizzly.NakamaNetworkWrapper.Abstractions
{
    public interface ICoroutineRunner
    {
        Coroutine StartCoroutine(IEnumerator routine);
    
        void StopCoroutine(Coroutine routine);
    }
}