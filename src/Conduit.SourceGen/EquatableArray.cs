using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.SourceGen;

/// <summary>
/// A small immutable array wrapper with structural (value) equality, suitable for use in an
/// incremental source generator's cached model. Two instances are equal when they contain equal
/// elements in the same order. <c>default</c> and an empty array are treated as equal/empty.
/// </summary>
/// <typeparam name="T">A value-equatable element type (string, enum, or a record).</typeparam>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly T[]? items;

    /// <summary>Creates an <see cref="EquatableArray{T}"/> over the given items.</summary>
    public EquatableArray(T[] items)
    {
        this.items = items;
    }

    private T[] Items => this.items ?? Array.Empty<T>();

    /// <inheritdoc/>
    public int Count => this.Items.Length;

    /// <inheritdoc/>
    public T this[int index] => this.Items[index];

    /// <inheritdoc/>
    public bool Equals(EquatableArray<T> other) => this.Items.SequenceEqual(other.Items);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in this.Items)
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)this.Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.Items.GetEnumerator();

    /// <summary>Returns <c>true</c> when <paramref name="left"/> and <paramref name="right"/> contain equal elements in the same order.</summary>
    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    /// <summary>Returns <c>true</c> when <paramref name="left"/> and <paramref name="right"/> differ in elements or order.</summary>
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}

/// <summary>Extension helpers for building <see cref="EquatableArray{T}"/>.</summary>
public static class EquatableArray
{
    /// <summary>Materializes an enumerable into an <see cref="EquatableArray{T}"/> (always copies, so the
    /// result is independent of the source).</summary>
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source)
        => new(source.ToArray());
}
