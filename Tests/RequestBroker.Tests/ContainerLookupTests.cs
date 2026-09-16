using System.Collections.Generic;
using UnityEngine;
using valheimCLI;
using Xunit;

namespace valheimCLI.Tests
{

/// <summary>
/// Which prefabs the container census will look inside.
///
/// The case that matters is a container hung on a CHILD of the prefab rather
/// than on its root. The vanilla Cart is one: a root-only lookup reported no
/// container at a cart holding 123 stone, on the station, before and after a
/// save. A pure test cannot hold the real prefab, so it holds the shape.
/// </summary>
public class ContainerLookupTests
{
    [Fact]
    public void AContainerOnTheRootIsFound()
    {
        GameObject chest = new GameObject();
        chest.Add(new Container());
        Assert.True(ContainerLookup.HoldsAContainer(chest));
    }

    [Fact]
    public void AContainerOnAChildIsFoundToo()
    {
        // The cart's shape: nothing on the root, the container one level down.
        GameObject cart = new GameObject();
        GameObject box = new GameObject();
        box.Add(new Container());
        cart.AddChild(box);

        Assert.True(ContainerLookup.HoldsAContainer(cart),
            "a container on a child was missed, which is the cart bug");
    }

    [Fact]
    public void AnInactiveChildStillCounts()
    {
        // GetComponentInChildren skips inactive objects unless asked, and a
        // prefab asset is not an active scene object.
        GameObject cart = new GameObject();
        GameObject box = new GameObject { Active = false };
        box.Add(new Container());
        cart.AddChild(box);

        Assert.True(ContainerLookup.HoldsAContainer(cart));
    }

    [Fact]
    public void SomethingWithNoContainerAnywhereIsNotOne()
    {
        GameObject rock = new GameObject();
        rock.AddChild(new GameObject());
        Assert.False(ContainerLookup.HoldsAContainer(rock));
    }

    [Fact]
    public void AMissingPrefabIsNotAContainer()
    {
        Assert.False(ContainerLookup.HoldsAContainer(null));
    }
}

}

// Minimal runtime seams. ContainerLookup itself is compiled unchanged into this
// suite; the plugin build separately checks the real game assembly.
public sealed class Container { }

namespace UnityEngine
{

public sealed class GameObject
{
    private readonly List<object> components = new List<object>();
    private readonly List<GameObject> children = new List<GameObject>();
    public bool Active = true;

    public void Add(object component) => components.Add(component);
    public void AddChild(GameObject child) => children.Add(child);

    /// <summary>Unity's rule: the root and every descendant; inactive ones only when asked.</summary>
    public T? GetComponentInChildren<T>(bool includeInactive = false) where T : class
    {
        if (!Active && !includeInactive)
            return null;
        foreach (object component in components)
            if (component is T match)
                return match;
        foreach (GameObject child in children)
        {
            T? found = child.GetComponentInChildren<T>(includeInactive);
            if (found != null)
                return found;
        }
        return null;
    }

    public T? GetComponent<T>() where T : class
    {
        foreach (object component in components)
            if (component is T match)
                return match;
        return null;
    }
}

}
