using System.Reflection;
using Nemus.Domain.Monetary;
using Xunit;

namespace Nemus.Tests.Domain;

/// <summary>
/// PILAR 2, cobrado por reflexao.
///
/// Nao basta escrever Money com long e confiar na disciplina. Este teste
/// varre o assembly de dominio inteiro - campos, propriedades, parametros,
/// retornos e construtores - e falha se aparecer decimal, double ou float em
/// qualquer lugar.
///
/// Consequencia pratica: nao ha onde guardar nem por onde passar dinheiro em
/// ponto flutuante. Se alguem acrescentar uma sobrecarga
/// "FromDecimal(decimal)" tres anos depois, a suite acusa no mesmo dia.
/// </summary>
public sealed class NoFloatingPointArchitectureTests
{
    private static readonly Type[] Banned = [typeof(decimal), typeof(double), typeof(float)];

    [Fact]
    public void Dominio_nao_expoe_decimal_double_nem_float_em_lugar_nenhum()
    {
        Assembly domain = typeof(Money).Assembly;
        var violations = new List<string>();

        foreach (Type type in domain.GetTypes())
        {
            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (FieldInfo field in type.GetFields(Flags))
            {
                if (IsBanned(field.FieldType))
                {
                    violations.Add($"campo {type.FullName}.{field.Name} : {field.FieldType.Name}");
                }
            }

            foreach (PropertyInfo property in type.GetProperties(Flags))
            {
                if (IsBanned(property.PropertyType))
                {
                    violations.Add(
                        $"propriedade {type.FullName}.{property.Name} : {property.PropertyType.Name}");
                }
            }

            foreach (MethodInfo method in type.GetMethods(Flags))
            {
                if (IsBanned(method.ReturnType))
                {
                    violations.Add(
                        $"retorno de {type.FullName}.{method.Name} : {method.ReturnType.Name}");
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (IsBanned(parameter.ParameterType))
                    {
                        violations.Add(
                            $"parametro {parameter.Name} de {type.FullName}.{method.Name} "
                            + $": {parameter.ParameterType.Name}");
                    }
                }
            }

            foreach (ConstructorInfo constructor in type.GetConstructors(Flags))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    if (IsBanned(parameter.ParameterType))
                    {
                        violations.Add(
                            $"parametro {parameter.Name} do construtor de {type.FullName} "
                            + $": {parameter.ParameterType.Name}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Ponto flutuante encontrado no dominio:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Money_guarda_o_valor_como_long()
    {
        PropertyInfo minorUnits = typeof(Money).GetProperty(nameof(Money.MinorUnits))!;
        Assert.Equal(typeof(long), minorUnits.PropertyType);
    }

    [Fact]
    public void Money_nao_tem_conversao_implicita_de_numero()
    {
        // Conversao implicita seria a porta dos fundos: "Money m = 10.5;"
        MethodInfo[] conversions = typeof(Money)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .ToArray();

        Assert.Empty(conversions);
    }

    private static bool IsBanned(Type type)
    {
        Type current = type;

        if (current.IsByRef || current.IsPointer || current.IsArray)
        {
            current = current.GetElementType() ?? current;
        }

        Type? underlying = Nullable.GetUnderlyingType(current);
        if (underlying is not null)
        {
            current = underlying;
        }

        if (Banned.Contains(current))
        {
            return true;
        }

        return current.IsGenericType
            && current.GetGenericArguments().Any(IsBanned);
    }
}
