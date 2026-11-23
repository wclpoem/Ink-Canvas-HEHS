// @parity src/InkCanvas.Platform/System/SoftwareLauncher.cs（C# 侧无启动器实现，仅同层平台服务门面）
import { common, Want } from '@kit.AbilityKit';

export function openExternalUrl(context: common.UIAbilityContext, url: string): void {
  const want: Want = { action: 'ohos.want.action.viewData', uri: url };
  context.startAbility(want).catch((e: Object) => {
    void e;
  });
}
