// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 WineExplorer contributors
//
// The static helper classes of Windows.UI.Xaml for the Windows.Foundation structs (SizeHelper, ...): plain arithmetic
// on the ABI structs, with WinUI's semantics (an empty Size is negative infinity in both dimensions; negative
// dimensions are invalid).
namespace XamlShim.Abi;

public unsafe partial class UI_Xaml_SizeHelper_Factory
{
    const int E_INVALIDARG = unchecked((int)0x80070057);

    int IUI_Xaml_ISizeHelperStatics_Abi.get_Empty(Foundation_Size* result)
    {
        *result = new Foundation_Size { Width = float.NegativeInfinity, Height = float.NegativeInfinity };
        return 0;
    }

    int IUI_Xaml_ISizeHelperStatics_Abi.FromDimensions(float width, float height, Foundation_Size* result)
    {
        if (width < 0 || height < 0 || float.IsNaN(width) || float.IsNaN(height)) return E_INVALIDARG;
        *result = new Foundation_Size { Width = width, Height = height };
        return 0;
    }

    int IUI_Xaml_ISizeHelperStatics_Abi.GetIsEmpty(Foundation_Size target, byte* result)
    {
        *result = (byte)(float.IsNegativeInfinity(target.Width) ? 1 : 0);
        return 0;
    }

    int IUI_Xaml_ISizeHelperStatics_Abi.Equals(Foundation_Size target, Foundation_Size value, byte* result)
    {
        *result = (byte)(target.Width.Equals(value.Width) && target.Height.Equals(value.Height) ? 1 : 0);
        return 0;
    }
}
