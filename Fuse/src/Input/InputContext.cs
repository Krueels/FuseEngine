using System.Collections.Generic;

namespace Fuse.Input;

public enum InputContext
{
    None = 0,
    Gameplay = 1,      // Movimento, interação, pickup (E, LMB para arremessar)
    Weapon = 2,        // Atirar, recarregar, trocar arma (LMB, R, 1-9)
    UI = 3,            // Menus, console, editor (bloqueia tudo exceto Debug)
    Noclip = 4,        // Voo livre (substitui Movement)
    Debug = 5          // Teclas debug (F1, F2, F5, F9, G) - sempre ativo
}

public static class InputManager
{
    private static readonly Dictionary<InputContext, int> _contextPriorities = new()
    {
        { InputContext.Debug, 100 },
        { InputContext.UI, 90 },
        { InputContext.Weapon, 80 },
        { InputContext.Noclip, 70 },
        { InputContext.Gameplay, 60 },
        { InputContext.None, 0 }
    };

    private static readonly HashSet<InputContext> _activeContexts = new();

    public static InputContext CurrentContext => GetHighestPriorityContext();

    public static bool IsContextActive(InputContext ctx) => _activeContexts.Contains(ctx);

    public static bool RequestContext(InputContext ctx)
    {
        if (_activeContexts.Contains(ctx))
            return true;

        // Verifica se há contexto de maior prioridade ativo que bloqueia este
        foreach (var active in _activeContexts)
        {
            if (_contextPriorities[active] > _contextPriorities[ctx])
                return false; // Bloqueado por contexto de maior prioridade
        }

        _activeContexts.Add(ctx);
        return true;
    }

    public static void ReleaseContext(InputContext ctx)
    {
        _activeContexts.Remove(ctx);
    }

    public static void ClearAllContexts()
    {
        _activeContexts.Clear();
    }

    private static InputContext GetHighestPriorityContext()
    {
        InputContext highest = InputContext.None;
        int maxPriority = -1;

        foreach (var ctx in _activeContexts)
        {
            if (_contextPriorities[ctx] > maxPriority)
            {
                maxPriority = _contextPriorities[ctx];
                highest = ctx;
            }
        }

        return highest;
    }
}