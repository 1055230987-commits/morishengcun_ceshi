using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 运行时的交互对象抽象。以后树、箱子、门、NPC、工作台都实现它。
    /// 当前阶段只有「能被识别」，不实现具体交互内容。
    ///
    /// 设计要点（为联机和存档留口子）：
    ///   - 交互入口是 Interact(actor, request)，不是「本地玩家按了 E」；
    ///     actor 可以是本地玩家、AI、也可以是网络同步过来的另一个玩家。
    ///   - 返回 InteractionResult 而不是直接改状态，方便以后做交互校验/回滚/网络确认。
    /// </summary>
    public interface IInteractable
    {
        /// <summary>稳定 ID。存档、联机同步都依赖它，不要用 GetInstanceID()。</summary>
        string ObjectId { get; }

        /// <summary>交互判定用的世界坐标（一般是物体中心或交互点）。探测器靠它算距离与朝向。</summary>
        Vector3 WorldPosition { get; }

        /// <summary>交互提示文本（以后做 UI 时直接取用，例如「打开箱子」「砍伐」）。</summary>
        string InteractionPrompt { get; }

        /// <summary>该 actor 当前是否能交互（距离、状态、权限等由实现方判断）。</summary>
        bool CanInteract(GameObject actor);

        /// <summary>执行交互。</summary>
        InteractionResult Interact(GameObject actor);
    }

    /// <summary>交互请求。当前只带 actor 和来源；以后可扩展为带目标格子、手持物品等。</summary>
    public struct InteractionRequest
    {
        public GameObject Actor;
        /// <summary>触发来源标识（"local-input" / "ai" / "network"），仅用于日志与规则判断。</summary>
        public string Source;
    }

    /// <summary>交互结果。</summary>
    public struct InteractionResult
    {
        public bool Succeeded;
        /// <summary>失败原因或成功提示，交给上层决定是否弹 UI。</summary>
        public string Message;

        public static InteractionResult Success(string message = null)
        {
            return new InteractionResult { Succeeded = true, Message = message };
        }

        public static InteractionResult Failed(string message)
        {
            return new InteractionResult { Succeeded = false, Message = message };
        }
    }
}
