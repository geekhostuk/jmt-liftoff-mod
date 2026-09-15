using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace JmtLiftoffMod.Features.MultiplayerTrackControl;

internal static class ReflectionHelper
{
    public static object? GetMemberValue(object? instance, string memberName)
    {
        TryGetMemberValue(instance, memberName, out var value, out _);
        return value;
    }

    public static bool TryGetMemberValue(object? instance, string memberName, out object? value, out Exception? exception)
    {
        value = null;
        exception = null;

        if (instance == null)
            return false;

        var type = instance as Type ?? instance.GetType();
        var member = FindMember(type, memberName);
        if (member is PropertyInfo property)
        {
            try
            {
                value = property.GetValue(instance is Type ? null : instance, null);
                return true;
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        if (member is FieldInfo field)
        {
            try
            {
                value = field.GetValue(instance is Type ? null : instance);
                return true;
            }
            catch (Exception ex)
            {
                exception = ex;
                return false;
            }
        }

        return false;
    }

    private static readonly ConcurrentDictionary<(Type, string), MemberInfo?> Members = new();

    /// <summary>
    /// A type's property of that name, else its field, looked up once: the host-state poll
    /// reads the same few members off the same objects several times a second. A lookup that
    /// throws is not remembered, so it throws again as it always did.
    /// </summary>
    private static MemberInfo? FindMember(Type type, string memberName) =>
        Members.GetOrAdd((type, memberName), key =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            return (MemberInfo?)key.Item1.GetProperty(key.Item2, flags) ?? key.Item1.GetField(key.Item2, flags);
        });

    public static Type? GetMemberType(object? instance, string memberName)
    {
        if (instance == null)
            return null;

        var type = instance as Type ?? instance.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var property = type.GetProperty(memberName, flags);
        if (property != null)
            return property.PropertyType;

        var field = type.GetField(memberName, flags);
        return field?.FieldType;
    }

    public static bool SetMemberValue(object? instance, string memberName, object? value)
    {
        if (instance == null)
            return false;

        var type = instance.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var property = type.GetProperty(memberName, flags);
        if (property != null && property.CanWrite)
        {
            property.SetValue(instance, value, null);
            return true;
        }

        var field = type.GetField(memberName, flags);
        if (field == null)
            return false;

        field.SetValue(instance, value);
        return true;
    }

    public static MethodInfo? FindMethod(Type? type, string name, int? parameterCount = null)
    {
        if (type == null)
            return null;

        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == name && (!parameterCount.HasValue || method.GetParameters().Length == parameterCount.Value));
    }

    public static MethodInfo? FindDeclaredMethod(Type? type, string name, int? parameterCount = null)
    {
        if (type == null)
            return null;

        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == name && (!parameterCount.HasValue || method.GetParameters().Length == parameterCount.Value));
    }

    public static object? InvokeMethod(object instance, string name, params object?[] args)
    {
        var method = FindMethod(instance.GetType(), name, args.Length);
        return method?.Invoke(instance, args);
    }

    public static string FormatMethodSignature(MethodBase method)
    {
        var parameters = method.GetParameters();
        var parameterText = string.Join(", ", parameters.Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}"));
        var returnType = method is MethodInfo methodInfo ? methodInfo.ReturnType.Name : "void";
        return $"{returnType} {method.DeclaringType?.FullName}.{method.Name}({parameterText})";
    }

    /// <summary>
    /// Optional callback incremented by the executor's diagnostics tracker
    /// each time FindObjectsOfTypeAll is called.
    /// </summary>
    public static Action? OnFindObjectsOfTypeAllCalled;

    public static List<object> GetLiveObjects(Type? type)
    {
        if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type))
            return new List<object>();

        OnFindObjectsOfTypeAllCalled?.Invoke();
        return Resources.FindObjectsOfTypeAll(type).Cast<object>().ToList();
    }

    public static bool TryGetNestedString(object? value, out string text, params string[] memberPath)
    {
        text = string.Empty;
        var current = value;
        foreach (var memberName in memberPath)
        {
            current = GetMemberValue(current, memberName);
            if (current == null)
                return false;
        }

        if (current == null)
            return false;

        text = current as string ?? current.ToString() ?? string.Empty;
        return text.Length > 0;
    }

    public static string DescribeObjectIdentity(object? value)
    {
        if (value == null)
            return "<null>";

        if (value is UnityEngine.Object unityObject)
            return $"{unityObject.GetType().FullName}#{unityObject.GetInstanceID()}";

        return $"{value.GetType().FullName}@{value.GetHashCode():X}";
    }

    public static IEnumerable<object> EnumerateAsObjects(object? value)
    {
        if (value == null || value is string)
            return Enumerable.Empty<object>();

        if (value is IEnumerable enumerable)
        {
            var results = new List<object>();
            foreach (var item in enumerable)
            {
                if (item != null)
                    results.Add(item);
            }

            return results;
        }

        return Enumerable.Empty<object>();
    }

    public static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            null => false,
            int integer => integer != 0,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true
        };
    }

    public static string SafeDescribe(Func<object?, string> describe, object? value)
    {
        try
        {
            return describe(value);
        }
        catch (Exception ex)
        {
            return $"<describe-error:{ex.GetType().Name}:{ex.Message}>";
        }
    }
}
