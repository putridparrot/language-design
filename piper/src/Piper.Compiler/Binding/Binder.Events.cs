using System.Collections.Immutable;
using Piper.Syntax;

namespace Piper.Binding;

/// <summary>
/// A class's events, <c>event Changed: EventHandler;</c>: C# field-like events. Anyone who can see one adds a handler
/// with <c>+=</c> and removes it with <c>-=</c>; only its class raises it, by calling it, <c>Changed(this, args)</c>,
/// which calls each handler, if there are any. An event's type is a delegate returning nothing: a .NET one such as
/// <c>EventHandler&lt;T&gt;</c> or <c>PropertyChangedEventHandler</c>, or a function type such as <c>(string) =&gt; unit</c>.
/// A class implements a .NET interface's event (<c>INotifyPropertyChanged.PropertyChanged</c>) by declaring it.
/// </summary>
public sealed partial class Binder
{
    /// <summary>A class's events, bound with its other members; <paramref name="names"/> are those already taken.</summary>
    private ImmutableArray<EventSymbol> BindClassEvents(ClassSymbol @class, FileContext file, HashSet<string> names)
    {
        var events = ImmutableArray.CreateBuilder<EventSymbol>();
        foreach (var syntax in @class.Declaration.Events)
        {
            TypeSymbol type = TypeSymbol.Error;
            WithScope(TypeParameterScope(@class, file), () => type = BindType(syntax.Type));
            if (!syntax.Attributes.IsEmpty)
                _diagnostics.ReportClassMember(syntax.Attributes[0].Span, "Events can't have attributes yet.");
            if (syntax.Name.IsMissing)
                continue;
            if (type != TypeSymbol.Error && !(DelegateSignature(type) is { Return: var returned } && returned == TypeSymbol.Unit))
                _diagnostics.ReportEventType(syntax.Type.Span, type);
            if (!names.Add(syntax.Name.Text) || @class.Declaration.Fields.Any(f => f.Name.Text == syntax.Name.Text))
            {
                _diagnostics.ReportDuplicateDeclaration(syntax.Name.Span, syntax.Name.Text);
                continue;
            }
            events.Add(Declared(new EventSymbol(syntax.Name.Text, type, @class)
            {
                IsPrivate = syntax.Private is not null,
                Documentation = syntax.Documentation,
                DeclaringFile = file.Source,
            }, syntax.Name));
        }
        return events.ToImmutable();
    }

    /// <summary>The event called <paramref name="name"/> that <paramref name="type"/> (a Piper class) declares or inherits.</summary>
    private static EventSymbol? FindEvent(TypeSymbol type, string name) =>
        type.OriginalDefinition is ClassSymbol @class ? @class.SelfAndBases().SelectMany(c => c.Events).FirstOrDefault(e => e.Name == name) : null;

    /// <summary>An event's delegate type, for an instance of <paramref name="receiver"/>'s type (a generic class's events may use its type parameters).</summary>
    private static TypeSymbol EventType(EventSymbol @event, TypeSymbol receiver) =>
        TypeSubstitution.Substitute(@event.Type, TypeSubstitution.MapFor(TypeSubstitution.AsBase(receiver, @event.Owner)));

    /// <summary>`this`, for an event named bare in its class's methods; null outside one.</summary>
    private BoundExpression? This() =>
        _scope.Lookup("this") is VariableSymbol { Kind: VariableKind.This } self ? new BoundVariableExpression(self) : null;

    /// <summary>A private event is used only in its class.</summary>
    private bool EventAccessible(EventSymbol @event, Token name)
    {
        if (!@event.IsPrivate || _currentClass == @event.Owner)
            return true;
        _diagnostics.ReportPrivateMember(name.Span, @event.Name, @event.Owner.Name);
        return false;
    }

    /// <summary>
    /// <c>account.Changed += handler</c>, <c>Changed -= handler</c>: the handler (a lambda or function) is converted to
    /// the event's delegate type. Null if <paramref name="syntax"/> isn't assigning to an event.
    /// </summary>
    private BoundExpression? BindEventSubscription(AssignmentExpressionSyntax syntax)
    {
        BoundExpression? receiver;
        Token name;
        EventSymbol? @event;
        switch (syntax.Target)
        {
            case NameExpressionSyntax { TypeArguments.IsEmpty: true } bare when _scope.Lookup(bare.Identifier.Text) is EventSymbol named:
                (receiver, name, @event) = (This(), bare.Identifier, named);
                break;
            case MemberAccessExpressionSyntax member when Trial(() => ResolveContainer(member.Target), out _) is null &&
                                                          Trial(() => BindExpression(member.Target), out _) is { Type: var type } &&
                                                          FindEvent(type, member.Member.Text) is { } found:
                (receiver, name, @event) = (RequireValue(BindExpression(member.Target), member.Target.Span), member.Member, found);
                break;
            default:
                return null;
        }
        Record(name, @event);
        // The handler is bound as one even after an error, so a lambda's parameter types are known.
        var handlerType = receiver is null ? @event.Type : EventType(@event, receiver.Type);
        if (receiver is null || !EventAccessible(@event, name))
        {
            BindExpression(syntax.Value, handlerType);
            return new BoundErrorExpression();
        }
        if (syntax.Operator.Kind is not (TokenKind.PlusEquals or TokenKind.MinusEquals))
        {
            BindExpression(syntax.Value, handlerType);
            _diagnostics.ReportEventMisused(name.Span, @event.Name);
            return new BoundErrorExpression();
        }
        var handler = Convert(RequireValue(BindEventHandler(syntax.Value, handlerType), syntax.Value.Span), handlerType, syntax.Value.Span);
        return new BoundEventSubscription(receiver, @event, syntax.Operator.Kind == TokenKind.PlusEquals, handler);
    }

    /// <summary>
    /// <c>Changed(this, EventArgs.Empty)</c> or <c>other.Changed(…)</c> in the event's class: each handler is called with
    /// the arguments, as the delegate's parameters take them. Null if <paramref name="syntax"/> doesn't call an event.
    /// </summary>
    private BoundExpression? BindEventRaise(CallExpressionSyntax syntax)
    {
        BoundExpression? receiver;
        Token name;
        EventSymbol? @event;
        switch (syntax.Callee)
        {
            case NameExpressionSyntax { TypeArguments.IsEmpty: true } bare when _scope.Lookup(bare.Identifier.Text) is EventSymbol named:
                (receiver, name, @event) = (This(), bare.Identifier, named);
                break;
            case MemberAccessExpressionSyntax member when Trial(() => ResolveContainer(member.Target), out _) is null &&
                                                          Trial(() => BindExpression(member.Target), out _) is { Type: var type } &&
                                                          FindEvent(type, member.Member.Text) is { } found:
                (receiver, name, @event) = (RequireValue(BindExpression(member.Target), member.Target.Span), member.Member, found);
                break;
            default:
                return null;
        }
        Record(name, @event);
        if (receiver is null || _currentClass != @event.Owner)
        {
            BindArgumentsForDiagnostics(syntax.Arguments);
            _diagnostics.ReportEventRaisedOutside(name.Span, @event.Name, @event.Owner.Name);
            return new BoundErrorExpression();
        }
        if (DelegateSignature(EventType(@event, receiver.Type)) is not { } signature)
            return BindArgumentsForDiagnostics(syntax.Arguments);
        var arguments = BindArguments(syntax.Span, @event.Name, signature.Parameters, [], new(), syntax.Arguments);
        return arguments is { } bound ? new BoundEventRaise(receiver, @event, bound) : new BoundErrorExpression();
    }
}
